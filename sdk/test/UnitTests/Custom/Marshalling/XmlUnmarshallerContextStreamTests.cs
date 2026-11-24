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
using System.Collections.Generic;
using System.IO;
using System.Text;
using Amazon.Runtime.Internal.Transform;
using Amazon.Runtime.Internal.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests.Custom.Marshalling
{
    /// <summary>
    /// Pins how XmlUnmarshallerContext exposes, reads and releases its response stream: the Stream
    /// property, the stream position before any XML is read, BOM handling, disposal with and without
    /// reading, and the exceptions for null, unreadable and disposed inputs.
    /// </summary>
    [TestClass]
    public class XmlUnmarshallerContextStreamTests
    {
        private const string Xml = "<PublishResponse xmlns=\"http://sns.amazonaws.com/doc/2010-03-31/\"><PublishResult><MessageId>m-1</MessageId></PublishResult><ResponseMetadata><RequestId>r-1</RequestId></ResponseMetadata></PublishResponse>";

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void StreamIsTheResponseStreamAndIsNotRead()
        {
            var body = new TrackingStream(Encoding.UTF8.GetBytes(Xml));
            using (var context = new XmlUnmarshallerContext(body, false, null))
            {
                Assert.AreSame(body, context.Stream);
                Assert.AreSame(body, context.Stream);
                Assert.AreEqual(0, body.Position);
                Assert.AreEqual(0, body.ReadCalls);
                Assert.IsFalse(body.IsClosed);
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void StreamIsTheCachingWrapperForExceptionsAndLogging()
        {
            foreach (var (maintain, isException) in new[] { (false, true), (true, false), (true, true) })
            {
                var body = new TrackingStream(Encoding.UTF8.GetBytes(Xml));
                using (var context = new XmlUnmarshallerContext(body, maintain, null, isException))
                {
                    Assert.IsInstanceOfType(context.Stream, typeof(CachingWrapperStream));
                    Assert.AreSame(context.Stream, context.Stream);
                    Assert.AreEqual(0, body.ReadCalls);
                }
                Assert.IsTrue(body.IsClosed);
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void DisposeWithoutReadingClosesTheStreamOnce()
        {
            var body = new TrackingStream(Encoding.UTF8.GetBytes(Xml));
            var context = new XmlUnmarshallerContext(body, false, null);
            context.Dispose();
            Assert.IsTrue(body.IsClosed);
            Assert.AreEqual(1, body.CloseCalls);
            Assert.AreEqual(0, body.ReadCalls);

            context.Dispose();
            Assert.AreEqual(1, body.CloseCalls);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void DisposeAfterReadingClosesTheStream()
        {
            var body = new TrackingStream(Encoding.UTF8.GetBytes(Xml));
            var context = new XmlUnmarshallerContext(body, false, null);
            Assert.AreEqual("m-1", ReadMembers(context)["MessageId"]);
            context.Dispose();
            Assert.IsTrue(body.IsClosed);
            Assert.AreEqual(1, body.CloseCalls);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void ReadsXmlWithAndWithoutByteOrderMark()
        {
            var bodies = new List<byte[]>
            {
                Encoding.UTF8.GetBytes(Xml),
                Concat(new byte[] { 0xEF, 0xBB, 0xBF }, Encoding.UTF8.GetBytes(Xml)),
                Concat(new byte[] { 0xFF, 0xFE }, Encoding.Unicode.GetBytes(Xml)),
                Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + Xml.Replace("m-1", "caf\u00e9 \u65e5\u672c \U0001F600")),
            };

            foreach (var body in bodies)
            {
                using (var context = new XmlUnmarshallerContext(new MemoryStream(body), false, null))
                {
                    Assert.IsTrue(context.IsStartOfDocument);
                    var members = ReadMembers(context);
                    Assert.IsTrue(members["MessageId"] == "m-1" || members["MessageId"] == "caf\u00e9 \u65e5\u672c \U0001F600");
                    Assert.AreEqual("r-1", members["RequestId"]);
                }
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void ReadingContinuesFromWhereTheCallerLeftTheStream()
        {
            var bytes = Encoding.UTF8.GetBytes(Xml);
            using (var context = new XmlUnmarshallerContext(new MemoryStream(bytes), false, null))
            {
                Assert.AreEqual('<', (char)context.Stream.ReadByte());
                Assert.ThrowsExactly<System.Xml.XmlException>(() => ReadMembers(context));
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void NullStreamThrowsArgumentNullException()
        {
            var e = Assert.ThrowsExactly<ArgumentNullException>(() => new XmlUnmarshallerContext(null, false, null));
            Assert.AreEqual("stream", e.ParamName);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void UnreadableStreamThrowsArgumentException()
        {
            Assert.ThrowsExactly<ArgumentException>(() => new XmlUnmarshallerContext(new WriteOnlyStream(), false, null));
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void AlreadyClosedStreamThrowsArgumentException()
        {
            var body = new MemoryStream(Encoding.UTF8.GetBytes(Xml));
            body.Dispose();
            Assert.ThrowsExactly<ArgumentException>(() => new XmlUnmarshallerContext(body, false, null));
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void StreamAfterDisposeThrowsNullReferenceException()
        {
            foreach (var read in new[] { false, true })
            {
                var context = new XmlUnmarshallerContext(new MemoryStream(Encoding.UTF8.GetBytes(Xml)), false, null);
                if (read)
                    ReadMembers(context);
                context.Dispose();
                Assert.ThrowsExactly<NullReferenceException>(() => context.Stream);
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void ReadAfterDisposeThrowsXmlException()
        {
            foreach (var read in new[] { false, true })
            {
                var context = new XmlUnmarshallerContext(new MemoryStream(Encoding.UTF8.GetBytes(Xml)), false, null);
                if (read)
                    ReadMembers(context);
                context.Dispose();
                Assert.ThrowsExactly<System.Xml.XmlException>(() => context.Read());
            }
        }

        private static Dictionary<string, string> ReadMembers(XmlUnmarshallerContext context)
        {
            var members = new Dictionary<string, string>();
            while (context.Read())
            {
                if (context.IsStartElement && (context.TestExpression("MessageId", 3) || context.TestExpression("RequestId", 3)))
                    members[context.CurrentElementName] = StringUnmarshaller.Instance.Unmarshall(context);
            }
            return members;
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var result = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, result, 0, a.Length);
            Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
            return result;
        }

        private sealed class TrackingStream : MemoryStream
        {
            public TrackingStream(byte[] bytes) : base(bytes, false) { }

            public int ReadCalls { get; private set; }
            public int CloseCalls { get; private set; }
            public bool IsClosed => !CanRead;

            public override int Read(byte[] buffer, int offset, int count)
            {
                ReadCalls++;
                return base.Read(buffer, offset, count);
            }

#if NETCOREAPP
            public override int Read(Span<byte> buffer)
            {
                ReadCalls++;
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

        private sealed class WriteOnlyStream : MemoryStream
        {
            public override bool CanRead => false;
        }
    }
}
