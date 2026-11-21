/*
 * Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License").
 * You may not use this file except in compliance with the License.
 * A copy of the License is located at
 * 
 *  http://aws.amazon.com/apache2.0
 * 
 * or in the "license" file accompanying this file. This file is distributed
 * on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either
 * express or implied. See the License for the specific language governing
 * permissions and limitations under the License.
 */
using Amazon;
using Amazon.Runtime.Internal.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AWSSDK.UnitTests.Runtime
{
    /// <summary>
    /// Pins the exact bytes CachingWrapperStream caches: the bytes read, in order, taken from
    /// the requested offset of the caller's buffer, capped at the limit, and LoggableReadBytes
    /// equal to AllReadBytes.Take(limit).
    /// </summary>
    [TestClass]
    public class CachingWrapperStreamContentTests
    {
        private int _originalLogResponsesSizeLimit;

        [TestInitialize]
        public void Init()
        {
            _originalLogResponsesSizeLimit = AWSConfigs.LoggingConfig.LogResponsesSizeLimit;
        }

        [TestCleanup]
        public void Cleanup()
        {
            AWSConfigs.LoggingConfig.LogResponsesSizeLimit = _originalLogResponsesSizeLimit;
        }

        private static byte[] Data(int length)
        {
            var data = new byte[length];
            new Random(length).NextBytes(data);
            return data;
        }

        [TestMethod]
        [TestCategory("Runtime")]
        public void CachesExactBytesAtOffsets()
        {
            foreach (int? limit in new int?[] { null, 0, 1, 7, 100, 1000, 5000, -1 })
            {
                var data = Data(3000);
                var stream = new CachingWrapperStream(new MemoryStream(data), limit);
                var buffer = new byte[64];
                var random = new Random(1);
                int total = 0, n;
                // Varying offsets and counts, with garbage around the read range.
                do
                {
                    for (int i = 0; i < buffer.Length; i++) buffer[i] = 0xEE;
                    int offset = random.Next(0, 32);
                    int count = random.Next(0, buffer.Length - offset + 1);
                    n = stream.Read(buffer, offset, count);
                    CollectionAssert.AreEqual(data.Skip(total).Take(n).ToArray(), buffer.Skip(offset).Take(n).ToArray());
                    total += n;
                } while (n > 0 || total < data.Length);

                var expected = limit.HasValue ? data.Take(Math.Max(0, limit.Value)).ToArray() : data;
                CollectionAssert.AreEqual(expected, stream.AllReadBytes.ToArray(), "limit " + limit);
            }
        }

        [TestMethod]
        [TestCategory("Runtime")]
        public async Task ReadAsyncCachesExactBytes()
        {
            var data = Data(10000);
            var stream = new CachingWrapperStream(new MemoryStream(data), 6000);
            var buffer = new byte[1000];
            while (await stream.ReadAsync(buffer, 100, 900).ConfigureAwait(false) > 0) { }

            CollectionAssert.AreEqual(data.Take(6000).ToArray(), stream.AllReadBytes.ToArray());
        }

        [TestMethod]
        [TestCategory("Runtime")]
        public void LoggableReadBytesMatchesTake()
        {
            var data = Data(500);
            foreach (int? cacheLimit in new int?[] { null, 10, 500, 1000 })
            {
                foreach (int logLimit in new[] { 0, 1, 250, 500, 600, int.MaxValue, -5 })
                {
                    AWSConfigs.LoggingConfig.LogResponsesSizeLimit = logLimit;
                    var stream = new CachingWrapperStream(new MemoryStream(data), cacheLimit);
                    stream.CopyTo(Stream.Null);

                    var expected = stream.AllReadBytes.Take(cacheLimit ?? logLimit).ToList();
                    var actual = stream.LoggableReadBytes;
                    CollectionAssert.AreEqual(expected, actual, "cache " + cacheLimit + " log " + logLimit);
                    Assert.AreNotSame(stream.AllReadBytes, actual);
                }
            }
        }

        [TestMethod]
        [TestCategory("Runtime")]
        public void LoggableReadBytesIsACopy()
        {
            var stream = new CachingWrapperStream(new MemoryStream(Data(100)), 50);
            stream.CopyTo(Stream.Null);
            var loggable = stream.LoggableReadBytes;
            loggable.Clear();
            Assert.AreEqual(50, stream.AllReadBytes.Count);
        }
    }
}
