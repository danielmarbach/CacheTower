﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using CacheTower.Extensions;
using CacheTower.Internal;

namespace CacheTower
{
	/// <summary>
	/// A <see cref="CacheStack"/> is the backbone of caching for Cache Tower. This manages coordination between the various cache layers, manages the cache extensions and handles background refreshing.
	/// </summary>
	public class CacheStack : ICacheStack, IFlushableCacheStack, IExtendableCacheStack, IAsyncDisposable
	{
		private bool Disposed;

		private readonly CacheEntryKeyLock KeyLock = new();
		private readonly ICacheLayer[] CacheLayers;
		private readonly ExtensionContainer Extensions;

		/// <summary>
		/// Creates a new <see cref="CacheStack"/> with the given <paramref name="cacheLayers"/> and <paramref name="extensions"/>.
		/// </summary>
		/// <param name="cacheLayers">The cache layers to use for the current cache stack. The layers should be ordered from the highest priority to the lowest. At least one cache layer is required.</param>
		/// <param name="extensions">The cache extensions to use for the current cache stack.</param>
		public CacheStack(ICacheLayer[] cacheLayers, ICacheExtension[] extensions)
		{
			if (cacheLayers == null || cacheLayers.Length == 0)
			{
				throw new ArgumentException("There must be at least one cache layer", nameof(cacheLayers));
			}

			CacheLayers = cacheLayers;

			Extensions = new ExtensionContainer(extensions);
			Extensions.Register(this);
		}

		/// <summary>
		/// Helper for throwing if disposed.
		/// </summary>
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		protected void ThrowIfDisposed()
		{
			if (Disposed)
			{
				ThrowDisposedException();
			}

			static void ThrowDisposedException() => throw new ObjectDisposedException(nameof(CacheStack));
		}

		/// <inheritdoc/>
		public IReadOnlyList<ICacheLayer> GetCacheLayers()
		{
			return CacheLayers;
		}

		/// <inheritdoc/>
		public async ValueTask FlushAsync()
		{
			ThrowIfDisposed();

			for (int i = 0, l = CacheLayers.Length; i < l; i++)
			{
				var layer = CacheLayers[i];
				await layer.FlushAsync();
			}

			await Extensions.OnCacheFlushAsync();
		}

		/// <inheritdoc/>
		public async ValueTask CleanupAsync()
		{
			ThrowIfDisposed();

			for (int i = 0, l = CacheLayers.Length; i < l; i++)
			{
				var layer = CacheLayers[i];
				await layer.CleanupAsync();
			}
		}

		/// <inheritdoc/>
		public async ValueTask EvictAsync(string cacheKey)
		{
			ThrowIfDisposed();

			if (cacheKey == null)
			{
				throw new ArgumentNullException(nameof(cacheKey));
			}

			for (int i = 0, l = CacheLayers.Length; i < l; i++)
			{
				var layer = CacheLayers[i];
				await layer.EvictAsync(cacheKey);
			}

			await Extensions.OnCacheEvictionAsync(cacheKey);
		}

		/// <inheritdoc/>
		public async ValueTask<CacheEntry<T>> SetAsync<T>(string cacheKey, T value, TimeSpan timeToLive)
		{
			ThrowIfDisposed();

			if (cacheKey == null)
			{
				throw new ArgumentNullException(nameof(cacheKey));
			}

			var cacheEntry = new CacheEntry<T>(value, timeToLive);
			await InternalSetAsync(cacheKey, cacheEntry, CacheUpdateType.AddOrUpdateEntry);
			return cacheEntry;
		}

		/// <inheritdoc/>
		public async ValueTask SetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry)
		{
			ThrowIfDisposed();

			if (cacheKey == null)
			{
				throw new ArgumentNullException(nameof(cacheKey));
			}

			if (cacheEntry == null)
			{
				throw new ArgumentNullException(nameof(cacheEntry));
			}

			await InternalSetAsync(cacheKey, cacheEntry, CacheUpdateType.AddOrUpdateEntry);
		}

		private async ValueTask InternalSetAsync<T>(string cacheKey, CacheEntry<T> cacheEntry, CacheUpdateType cacheUpdateType)
		{
			for (int i = 0, l = CacheLayers.Length; i < l; i++)
			{
				var layer = CacheLayers[i];
				await layer.SetAsync(cacheKey, cacheEntry);
			}

			await Extensions.OnCacheUpdateAsync(cacheKey, cacheEntry.Expiry, cacheUpdateType);
		}

		/// <inheritdoc/>
		public async ValueTask<CacheEntry<T>?> GetAsync<T>(string cacheKey)
		{
			ThrowIfDisposed();

			if (cacheKey == null)
			{
				throw new ArgumentNullException(nameof(cacheKey));
			}

			for (var layerIndex = 0; layerIndex < CacheLayers.Length; layerIndex++)
			{
				var layer = CacheLayers[layerIndex];
				if (await layer.IsAvailableAsync(cacheKey))
				{
					var cacheEntry = await layer.GetAsync<T>(cacheKey);
					if (cacheEntry != default)
					{
						return cacheEntry;
					}
				}
			}

			return default;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private async ValueTask<(int LayerIndex, CacheEntry<T> CacheEntry)> GetWithLayerIndexAsync<T>(string cacheKey)
		{
			for (var layerIndex = 0; layerIndex < CacheLayers.Length; layerIndex++)
			{
				var layer = CacheLayers[layerIndex];
				if (await layer.IsAvailableAsync(cacheKey))
				{
					var cacheEntry = await layer.GetAsync<T>(cacheKey);
					if (cacheEntry != default)
					{
						return (layerIndex, cacheEntry);
					}
				}
			}

			return default;
		}

		/// <inheritdoc/>
		public async ValueTask<T> GetOrSetAsync<T>(string cacheKey, Func<T, Task<T>> valueFactory, CacheSettings settings)
		{
			ThrowIfDisposed();

			if (cacheKey == null)
			{
				throw new ArgumentNullException(nameof(cacheKey));
			}

			if (valueFactory == null)
			{
				throw new ArgumentNullException(nameof(valueFactory));
			}

			var currentTime = DateTimeProvider.Now;
			var cacheEntryPoint = await GetWithLayerIndexAsync<T>(cacheKey);
			if (cacheEntryPoint != default)
			{
				if (cacheEntryPoint.CacheEntry.Expiry > currentTime)
				{
					var cacheEntry = cacheEntryPoint.CacheEntry;
					if (settings.StaleAfter.HasValue && cacheEntry.GetStaleDate(settings) < currentTime)
					{
						//If the cache entry is stale, refresh the value in the background
						_ = RefreshValueAsync(cacheKey, valueFactory, settings, CacheEntryStatus.Stale);
					}
					else if (cacheEntryPoint.LayerIndex > 0)
					{
						//If a lower-level cache is missing the latest data, attempt to set it in the background
						_ = BackPopulateCacheAsync(cacheEntryPoint.LayerIndex, cacheKey, cacheEntry);
					}

					return cacheEntry.Value!;
				}
				else
				{
					//Refresh the value in the current thread because we only have expired data (we never return expired data)
					return (await RefreshValueAsync(cacheKey, valueFactory, settings, CacheEntryStatus.Expired))!.Value!;
				}
			}
			else
			{
				//Refresh the value in the current thread because we have no existing data
				return (await RefreshValueAsync(cacheKey, valueFactory, settings, CacheEntryStatus.Miss))!.Value!;
			}
		}

		private async ValueTask BackPopulateCacheAsync<T>(int fromIndexExclusive, string cacheKey, CacheEntry<T> cacheEntry)
		{
			if (KeyLock.AcquireLock(cacheKey))
			{
				try
				{
					for (; --fromIndexExclusive >= 0;)
					{
						var previousLayer = CacheLayers[fromIndexExclusive];
						if (await previousLayer.IsAvailableAsync(cacheKey))
						{
							await previousLayer.SetAsync(cacheKey, cacheEntry);
						}
					}
				}
				finally
				{
					KeyLock.ReleaseLock(cacheKey, cacheEntry);
				}
			}
		}

		private async ValueTask<CacheEntry<T>?> RefreshValueAsync<T>(string cacheKey, Func<T, Task<T>> asyncValueFactory, CacheSettings settings, CacheEntryStatus entryStatus)
		{
			if (KeyLock.AcquireLock(cacheKey))
			{
				try
				{
					var previousEntry = await GetAsync<T>(cacheKey);
					if (previousEntry != default && entryStatus == CacheEntryStatus.Miss && previousEntry.Expiry >= DateTimeProvider.Now)
					{
						//The Cache Stack will always return an unexpired value if one exists.
						//If we are told to refresh because one doesn't and we find one, we return the existing value, ignoring the refresh.
						//This can happen due to the race condition of getting the values out of the cache.
						//We can only do any of this because we have the local lock.
						KeyLock.ReleaseLock(cacheKey, previousEntry);
						return previousEntry;
					}

					return await Extensions.WithRefreshAsync(cacheKey, async state =>
					{
						var (previousEntry, asyncValueFactory, settings, entryStatus, cacheKey) = state;
						var oldValue = default(T);
						if (previousEntry != default)
						{
							oldValue = previousEntry.Value;
						}

						var value = await asyncValueFactory(oldValue!);
						var refreshedEntry = new CacheEntry<T>(value, settings.TimeToLive);
						var cacheUpdateType = entryStatus switch
						{
							CacheEntryStatus.Miss => CacheUpdateType.AddEntry,
							_ => CacheUpdateType.AddOrUpdateEntry
						};
						await InternalSetAsync(cacheKey, refreshedEntry, cacheUpdateType);

						KeyLock.ReleaseLock(cacheKey, refreshedEntry);

						return refreshedEntry;
					}, (previousEntry, asyncValueFactory, settings, entryStatus, cacheKey), settings);
				}
				catch (Exception e)
				{
					KeyLock.ReleaseLock(cacheKey, e);
					throw;
				}
			}
			else if (entryStatus != CacheEntryStatus.Stale)
			{
				//Last minute check to confirm whether waiting is required
				var currentEntry = await GetAsync<T>(cacheKey);
				if (currentEntry != null && currentEntry.GetStaleDate(settings) > DateTimeProvider.Now)
				{
					KeyLock.ReleaseLock(cacheKey, currentEntry);
					return currentEntry;
				}

				//Lock until we are notified to be unlocked
				return await KeyLock.WaitAsync(cacheKey) as CacheEntry<T>;
			}

			return default;
		}

		/// <summary>
		/// Disposes the current instance of <see cref="CacheStack"/> and all associated layers and extensions.
		/// </summary>
		/// <returns></returns>
		public async ValueTask DisposeAsync()
		{
			if (Disposed)
			{
				return;
			}

			foreach (var layer in CacheLayers)
			{
				if (layer is IDisposable disposableLayer)
				{
					disposableLayer.Dispose();
				}
				else if (layer is IAsyncDisposable asyncDisposableLayer)
				{
					await asyncDisposableLayer.DisposeAsync();
				}
			}

			await Extensions.DisposeAsync();

			Disposed = true;
		}
	}
}
