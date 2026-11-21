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
using System;
using System.IO;
using System.Linq;
using System.Text;
using Amazon.Runtime.Internal.Transform;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests.Custom.Marshalling
{
    /// <summary>
    /// Pins UnmarshallerContext.ResponseBody to the UTF-8 decoding of GetResponseBodyBytes() for the
    /// exception, response-logging and plain contexts, including partial reads, the logging size
    /// limit, multi-byte characters cut by that limit, and invalid UTF-8.
    /// </summary>
    [TestClass]
    public class UnmarshallerContextResponseBodyTests
    {
        private static readonly byte[][] Bodies =
        {
            Array.Empty<byte>(),
            Encoding.UTF8.GetBytes("{\"__type\":\"ThrottlingException\",\"message\":\"Rate exceeded\"}"),
            Encoding.UTF8.GetBytes("<Error><Code>NoSuchKey</Code><Message>caf\u00e9 \u65e5\u672c \U0001F600</Message></Error>"),
            new byte[] { 0x7b, 0xff, 0xfe, 0xc3, 0x28, 0xe2, 0x82, 0x7d, 0xf0, 0x9f, 0x98 }, // invalid and truncated sequences
            Encoding.UTF8.GetBytes(new string('\u00e9', 700) + "tail"), // 1404 bytes: over the 1024-byte logging limit, cut mid-character
            Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray(),
        };

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void JsonContextResponseBodyMatchesBodyBytes()
        {
            foreach (var body in Bodies)
            {
                foreach (var (maintain, isException) in new[] { (false, true), (true, true), (true, false), (false, false) })
                {
                    foreach (var readAll in new[] { true, false })
                    {
                        using (var context = new JsonUnmarshallerContext(new MemoryStream(body), maintain, null, isException))
                        {
                            Read(context.Stream, readAll);
                            AssertBody(context, body, maintain, isException, readAll);
                        }
                    }
                }
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void XmlContextResponseBodyMatchesBodyBytes()
        {
            foreach (var body in Bodies)
            {
                foreach (var (maintain, isException) in new[] { (false, true), (true, true), (true, false), (false, false) })
                {
                    foreach (var readAll in new[] { true, false })
                    {
                        using (var context = new XmlUnmarshallerContext(new MemoryStream(body), maintain, null, isException))
                        {
                            Read(context.Stream, readAll);
                            AssertBody(context, body, maintain, isException, readAll);
                        }
                    }
                }
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void ResponseBodyIsNotCachedBetweenReads()
        {
            var body = Encoding.UTF8.GetBytes("{\"message\":\"partial then full\"}");
            using (var context = new JsonUnmarshallerContext(new MemoryStream(body), false, null, isException: true))
            {
                Assert.AreEqual(string.Empty, context.ResponseBody);
                var buffer = new byte[5];
                Assert.AreEqual(5, context.Stream.Read(buffer, 0, 5));
                Assert.AreEqual("{\"mes", context.ResponseBody);
                Read(context.Stream, true);
                Assert.AreEqual("{\"message\":\"partial then full\"}", context.ResponseBody);
                Assert.AreNotSame(context.ResponseBody, context.ResponseBody);
            }
        }

        private static void Read(Stream stream, bool readAll)
        {
            var buffer = new byte[100];
            if (!readAll)
            {
                stream.Read(buffer, 0, 7);
                return;
            }
            while (stream.Read(buffer, 0, buffer.Length) > 0) { }
        }

        private static void AssertBody(UnmarshallerContext context, byte[] body, bool maintain, bool isException, bool readAll)
        {
            var label = $"len={body.Length} maintain={maintain} isException={isException} readAll={readAll}";
            var bytes = context.GetResponseBodyBytes();
            Assert.AreEqual(Encoding.UTF8.GetString(bytes, 0, bytes.Length), context.ResponseBody, label);

            int read = readAll ? body.Length : Math.Min(7, body.Length);
            int expectedLength = isException ? read : maintain ? Math.Min(read, Amazon.AWSConfigs.LoggingConfig.LogResponsesSizeLimit) : 0;
            Assert.AreEqual(Encoding.UTF8.GetString(body, 0, expectedLength), context.ResponseBody, label);
            if (expectedLength == 0)
            {
                Assert.AreSame(string.Empty, context.ResponseBody, label);
            }
        }
    }
}
