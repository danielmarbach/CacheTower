﻿using CacheTower.Providers.Memory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CacheTower.Tests
{
	[TestClass]
	public class CacheStackStressTests : TestBase
	{
		[DataRow(1000)]
		[DataRow(100000)]
		[DataTestMethod]
		public async Task SimulatenousGetOrSet_CacheMiss(int iterations)
		{
			var cacheStack = new CacheStack(null, new[] { new MemoryCacheLayer() }, Array.Empty<ICacheExtension>());

			Task<int> lastTask = null;

			for (var i = 0; i < iterations; i++)
			{
				var index = i;
				lastTask = cacheStack.GetOrSetAsync<int>("SimulatenousGetOrSet", async (oldValue, context) => {
					await Task.Delay(100);
					return index;
				}, new CacheSettings(TimeSpan.FromDays(1)));
			}

			var result = await lastTask;

			Assert.AreEqual(0, result);
			
			await DisposeOf(cacheStack);
		}


		[DataRow(1000)]
		[DataRow(100000)]
		[DataTestMethod]
		public async Task SimulatenousGetOrSet_CacheMiss_UniqueKeys(int iterations)
		{
			var cacheStack = new CacheStack(null, new[] { new MemoryCacheLayer() }, Array.Empty<ICacheExtension>());
			
			Task<int> lastTask = null;

			for (var i = 0; i < iterations; i++)
			{
				var index = i;
				lastTask = cacheStack.GetOrSetAsync<int>($"SimulatenousGetOrSet_{index}", async (oldValue, context) => {
					await Task.Delay(100);
					return index + 1;
				}, new CacheSettings(TimeSpan.FromDays(1)));
			}

			var result = await lastTask;

			Assert.AreEqual(iterations, result);

			await DisposeOf(cacheStack);
		}
	}
}
