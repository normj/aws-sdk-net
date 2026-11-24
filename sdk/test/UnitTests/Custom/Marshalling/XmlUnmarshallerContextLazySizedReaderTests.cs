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
using System.Text;
using Amazon.Runtime.Internal.Transform;
using AWSSDK_DotNet.UnitTests.TestTools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests.Custom.Marshalling
{
    /// <summary>
    /// Pins the combination of a StreamReader created when the XML is first read and a buffer sized to a
    /// small Content-Length: nothing is read or allocated against the stream before the first Read, the
    /// reader then asks the stream for at most the sized buffer, and disposal and post-dispose behavior
    /// match a context without response data.
    /// </summary>
    [TestClass]
    public class XmlUnmarshallerContextLazySizedReaderTests
    {
        private const string Xml = "<PublishResponse xmlns=\"http://sns.amazonaws.com/doc/2010-03-31/\"><PublishResult><MessageId>m-1</MessageId></PublishResult><ResponseMetadata><RequestId>r-1</RequestId></ResponseMetadata></PublishResponse>";

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void SmallBodyIsNotReadUntilTheXmlIsRead()
        {
            var bytes = Encoding.UTF8.GetBytes(Xml);
            var body = new TrackingStream(bytes);
            using (var context = new XmlUnmarshallerContext(body, false, ResponseData(bytes.Length)))
            {
                Assert.AreSame(body, context.Stream);
                Assert.AreEqual(0, body.ReadCalls);
                Assert.AreEqual(0, body.Position);

                Assert.AreEqual("m-1", ReadMessageId(context));
                Assert.IsTrue(body.ReadCalls > 0);
            }
            Assert.AreEqual(1, body.CloseCalls);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void SmallBodyIsReadWithTheSizedBuffer()
        {
            var bytes = Encoding.UTF8.GetBytes(Xml);
            Assert.IsTrue(bytes.Length > 128 && bytes.Length < 1024);

            // Content-Length under 1 KB: the reader asks for at most Content-Length bytes at a time.
            var sized = new TrackingStream(bytes);
            using (var context = new XmlUnmarshallerContext(sized, false, ResponseData(bytes.Length)))
            {
                Assert.AreEqual("m-1", ReadMessageId(context));
            }
            Assert.AreEqual(bytes.Length, sized.LargestReadRequest);

            // A Content-Length under 128 is raised to StreamReader's 128-byte minimum.
            var minimum = new TrackingStream(bytes);
            using (var context = new XmlUnmarshallerContext(minimum, false, ResponseData(10)))
            {
                Assert.AreEqual("m-1", ReadMessageId(context));
            }
            Assert.AreEqual(128, minimum.LargestReadRequest);

            // No response data, a 1 KB Content-Length or a compressed body keep StreamReader's 1 KB default.
            foreach (var responseData in new[] { null, ResponseData(1024), ResponseData(bytes.Length, "gzip") })
            {
                var unsized = new TrackingStream(bytes);
                using (var context = new XmlUnmarshallerContext(unsized, false, responseData))
                {
                    Assert.AreEqual("m-1", ReadMessageId(context));
                }
                Assert.AreEqual(1024, unsized.LargestReadRequest);
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void SmallBodyDisposedWithoutReadingClosesTheStreamOnce()
        {
            var bytes = Encoding.UTF8.GetBytes(Xml);
            var body = new TrackingStream(bytes);
            var context = new XmlUnmarshallerContext(body, false, ResponseData(bytes.Length));
            context.Dispose();
            Assert.AreEqual(1, body.CloseCalls);
            Assert.AreEqual(0, body.ReadCalls);

            context.Dispose();
            Assert.AreEqual(1, body.CloseCalls);
            Assert.ThrowsExactly<NullReferenceException>(() => context.Stream);
            Assert.ThrowsExactly<System.Xml.XmlException>(() => context.Read());
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void SmallBodyAfterReadAndDisposeThrowsAsBefore()
        {
            var bytes = Encoding.UTF8.GetBytes(Xml);
            var context = new XmlUnmarshallerContext(new MemoryStream(bytes), false, ResponseData(bytes.Length));
            Assert.AreEqual("m-1", ReadMessageId(context));
            context.Dispose();
            Assert.ThrowsExactly<NullReferenceException>(() => context.Stream);
            Assert.ThrowsExactly<System.Xml.XmlException>(() => context.Read());
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void SmallBodyReadingContinuesFromWhereTheCallerLeftTheStream()
        {
            var bytes = Encoding.UTF8.GetBytes(Xml);
            using (var context = new XmlUnmarshallerContext(new MemoryStream(bytes), false, ResponseData(bytes.Length)))
            {
                Assert.AreEqual('<', (char)context.Stream.ReadByte());
                Assert.ThrowsExactly<System.Xml.XmlException>(() => ReadMessageId(context));
            }
        }

        private static WebResponseData ResponseData(long contentLength, string contentEncoding = null)
        {
            var responseData = new WebResponseData();
            responseData.Headers["Content-Length"] = contentLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (contentEncoding != null)
                responseData.Headers["Content-Encoding"] = contentEncoding;
            return responseData;
        }

        private static string ReadMessageId(XmlUnmarshallerContext context)
        {
            string messageId = null;
            while (context.Read())
            {
                if (context.IsStartElement && context.TestExpression("MessageId", 3))
                    messageId = StringUnmarshaller.Instance.Unmarshall(context);
            }
            return messageId;
        }

        private sealed class TrackingStream : MemoryStream
        {
            public TrackingStream(byte[] bytes) : base(bytes, false) { }

            public int ReadCalls { get; private set; }
            public int CloseCalls { get; private set; }
            public int LargestReadRequest { get; private set; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                ReadCalls++;
                LargestReadRequest = Math.Max(LargestReadRequest, count);
                return base.Read(buffer, offset, count);
            }

#if NETCOREAPP
            public override int Read(Span<byte> buffer)
            {
                ReadCalls++;
                LargestReadRequest = Math.Max(LargestReadRequest, buffer.Length);
                return base.Read(buffer);
            }
#endif

            public override void Close()
            {
                if (CanRead)
                    CloseCalls++;
                base.Close();
            }
        }
    }
}
