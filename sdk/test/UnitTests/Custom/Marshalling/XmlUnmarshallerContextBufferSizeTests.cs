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
using System.Linq;
using System.Text;
using Amazon.Runtime.Internal.Transform;
using AWSSDK_DotNet.UnitTests.TestTools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests.Custom.Marshalling
{
    /// <summary>
    /// Pins that XmlUnmarshallerContext reads a body the same whatever Content-Length says: for bodies
    /// from 0 bytes to past the 1 KB default reader buffer, with multi-byte characters across every
    /// 128-byte boundary, BOMs, invalid UTF-8, a Content-Length that is wrong in either direction,
    /// and a compressed body, the node trace and text match the context with no response data.
    /// </summary>
    [TestClass]
    public class XmlUnmarshallerContextBufferSizeTests
    {
        private static IEnumerable<byte[]> Bodies()
        {
            yield return Array.Empty<byte>();
            yield return Encoding.UTF8.GetBytes("<R/>");
            yield return Encoding.UTF8.GetBytes("<PublishResponse xmlns=\"http://sns.amazonaws.com/doc/2010-03-31/\"><PublishResult><MessageId>94f20ce6-13c5-43a0-9a9e-ca52d816e90b</MessageId></PublishResult><ResponseMetadata><RequestId>f187a3c1-376f-11df-8963-01868b7c937a</RequestId></ResponseMetadata></PublishResponse>");
            // Multi-byte characters (2, 3 and 4 bytes) starting at every offset around the 128-byte minimum buffer.
            foreach (var ch in new[] { "\u00e9", "\u65e5", "\U0001F600" })
            {
                for (int pad = 100; pad < 140; pad++)
                {
                    yield return Encoding.UTF8.GetBytes("<R a=\"" + new string('x', pad) + ch + "\"><V>" + ch + new string('y', pad) + ch + "</V></R>");
                }
            }
            foreach (var size in new[] { 127, 128, 129, 255, 256, 511, 1000, 1023, 1024, 1025, 3000 })
            {
                var sb = new StringBuilder("<R>");
                int i = 0;
                while (Encoding.UTF8.GetByteCount(sb.ToString()) < size - 20)
                    sb.Append("<K>").Append(i++ % 3 == 0 ? "caf\u00e9" : "k" + i).Append("</K>");
                sb.Append("</R>");
                yield return Encoding.UTF8.GetBytes(sb.ToString());
            }
            var xml = "<R><V>caf\u00e9 \u65e5\u672c \U0001F600</V></R>";
            yield return new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(xml)).ToArray();
            yield return new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(xml)).ToArray();
            yield return new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(xml)).ToArray();
            yield return new byte[] { 0xFF, 0xFE, 0x00, 0x00 }.Concat(Encoding.UTF32.GetBytes(xml)).ToArray();
            yield return Encoding.UTF8.GetBytes("<R><V>a").Concat(new byte[] { 0xff, 0xc3, 0x28, 0xe2, 0x82 }).Concat(Encoding.UTF8.GetBytes("b</V></R>")).ToArray();
            yield return Encoding.UTF8.GetBytes("<R><V>truncated").Concat(new byte[] { 0xf0, 0x9f, 0x98 }).ToArray();
            yield return Encoding.UTF8.GetBytes("not xml at all");
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void TraceDoesNotDependOnContentLength()
        {
            int bodies = 0;
            foreach (var body in Bodies())
            {
                string expected = Trace(body, null);
                foreach (var contentLength in new long[] { body.Length, 0, 1, 127, 128, Math.Max(0, body.Length - 1), body.Length + 1, 1023, 1024, 5000 })
                {
                    foreach (var contentEncoding in new[] { null, "gzip" })
                    {
                        var responseData = new WebResponseData();
                        responseData.Headers["Content-Length"] = contentLength.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        if (contentEncoding != null)
                            responseData.Headers["Content-Encoding"] = contentEncoding;
                        Assert.AreEqual(expected, Trace(body, responseData), $"body of {body.Length} bytes, Content-Length {contentLength}, Content-Encoding {contentEncoding}");
                    }
                }
                bodies++;
            }
            Assert.IsTrue(bodies > 100, bodies.ToString());
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void EmptyResponseIsFlaggedAsBefore()
        {
            foreach (var (header, empty) in new[] { ("0", true), ("1", false), ("-1", false), ("x", false), ("", false) })
            {
                var responseData = new WebResponseData();
                responseData.Headers["Content-Length"] = header;
                using (var context = new XmlUnmarshallerContext(new MemoryStream(Array.Empty<byte>()), false, responseData))
                {
                    Assert.AreEqual(empty, context.IsEmptyResponse, header);
                    Assert.ThrowsExactly<System.Xml.XmlException>(() => context.Read());
                }
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void NullAndUnreadableStreamsThrowAsBefore()
        {
            var responseData = new WebResponseData();
            responseData.Headers["Content-Length"] = "10";
            var e = Assert.ThrowsExactly<ArgumentNullException>(() => new XmlUnmarshallerContext(null, false, responseData));
            Assert.AreEqual("stream", e.ParamName);
            var closed = new MemoryStream(new byte[10]);
            closed.Dispose();
            Assert.ThrowsExactly<ArgumentException>(() => new XmlUnmarshallerContext(closed, false, responseData));
        }

        private static string Trace(byte[] body, WebResponseData responseData)
        {
            var sb = new StringBuilder();
            using (var context = new XmlUnmarshallerContext(new MemoryStream(body), false, responseData))
            {
                try
                {
                    for (int i = 0; i < 2000; i++)
                    {
                        bool more = context.Read();
                        string kind = context.IsStartElement ? "S" : context.IsEndElement ? "E" : context.IsAttribute ? "A" : "N";
                        sb.Append(kind).Append(' ').Append(context.CurrentPath).Append(' ').Append(context.CurrentDepth)
                          .Append(" [").Append(context.ReadText()).Append(']');
                        if (!more)
                        {
                            sb.Append(" END");
                            break;
                        }
                        sb.Append('|');
                    }
                }
                catch (System.Xml.XmlException e)
                {
                    sb.Append(" XmlException ").Append(e.Message);
                }
            }
            return sb.ToString();
        }
    }
}
