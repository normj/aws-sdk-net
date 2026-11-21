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
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests.Custom.Marshalling
{
    /// <summary>
    /// Pins how XmlUnmarshallerContext.Read surfaces element attributes: the node sequence, paths,
    /// depths and text (attribute values included), attribute expressions, consecutive elements with
    /// different attribute counts, more attributes than the scan limit, and the ArgumentException for
    /// a local name used twice. The expected traces were recorded from the implementation that kept
    /// attributes in a Dictionary and a List.
    /// </summary>
    [TestClass]
    public class XmlUnmarshallerContextAttributeTests
    {
        private static string Attrs(string prefix, int count) =>
            string.Concat(Enumerable.Range(0, count).Select(i => " " + prefix + i + "=\"v" + i + "\""));

        public static IEnumerable<object[]> Documents => new[]
        {
            new object[] { "root-xmlns", "<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Name>b</Name><Contents><Key>k</Key></Contents></ListBucketResult>" },
            new object[] { "nested", "<R a=\"1\"><A x=\"1\" y=\"2\" z=\"&amp;&quot;&#x41;\">t</A><B/><C q=\"\">u</C><D><E k=\"v\"><F>w</F></E></D></R>" },
            new object[] { "prefixes", "<R xmlns=\"urn:d\" xmlns:a=\"urn:a\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"><Grantee xsi:type=\"CanonicalUser\" a:id=\"7\"><ID>i</ID></Grantee></R>" },
            new object[] { "counts", "<R" + Attrs("r", 5) + "><A" + Attrs("a", 1) + ">1</A><B>2</B><C" + Attrs("c", 3) + ">3</C><D" + Attrs("d", 7) + ">4</D><E" + Attrs("e", 2) + ">5</E></R>" },
            new object[] { "limit", "<R><A" + Attrs("a", 8) + ">1</A><B" + Attrs("b", 9) + ">2</B><C" + Attrs("c", 17) + ">3</C><D" + Attrs("d", 1) + ">4</D></R>" },
            new object[] { "empty-with-attributes", "<R><A x=\"1\"/><B y=\"2\">t</B><C z=\"3\"></C></R>" },
            new object[] { "whitespace", "<?xml version=\"1.0\"?>\n<R  a = \"1\"\n b='2'>\n  <A c=\"3\">x</A>\n</R>" },
        };

        private static readonly Dictionary<string, string> ExpectedTraces = new Dictionary<string, string>
        {
            ["root-xmlns"] = "S /ListBucketResult 1 []|A /ListBucketResult/@xmlns 1 [http://s3.amazonaws.com/doc/2006-03-01/]|S /ListBucketResult/Name 2 [b]|E /ListBucketResult 1 [b]|S /ListBucketResult/Contents 2 []|S /ListBucketResult/Contents/Key 3 [k]|E /ListBucketResult/Contents 2 [k]|E /ListBucketResult 1 [k]|E / 0 [k] END",
            ["nested"] = "S /R 1 []|A /R/@a 1 [1]|S /R/A 2 [t]|A /R/A/@x 2 [1]|A /R/A/@y 2 [2]|A /R/A/@z 2 [&\"A]|E /R 1 [t]|S /R/B 2 []|E /R 1 []|S /R/C 2 [u]|A /R/C/@q 2 []|E /R 1 [u]|S /R/D 2 []|S /R/D/E 3 []|A /R/D/E/@k 3 [v]|S /R/D/E/F 4 [w]|E /R/D/E 3 [w]|E /R/D 2 [w]|E /R 1 [w]|E / 0 [w] END",
            ["prefixes"] = "S /R 1 []|A /R/@xmlns 1 [urn:d]|A /R/@a 1 [urn:a]|A /R/@xsi 1 [http://www.w3.org/2001/XMLSchema-instance]|S /R/Grantee 2 []|A /R/Grantee/@type 2 [CanonicalUser]|A /R/Grantee/@id 2 [7]|S /R/Grantee/ID 3 [i]|E /R/Grantee 2 [i]|E /R 1 [i]|E / 0 [i] END",
            ["counts"] = "S /R 1 []|A /R/@r0 1 [v0]|A /R/@r1 1 [v1]|A /R/@r2 1 [v2]|A /R/@r3 1 [v3]|A /R/@r4 1 [v4]|S /R/A 2 [1]|A /R/A/@a0 2 [v0]|E /R 1 [1]|S /R/B 2 [2]|E /R 1 [2]|S /R/C 2 [3]|A /R/C/@c0 2 [v0]|A /R/C/@c1 2 [v1]|A /R/C/@c2 2 [v2]|E /R 1 [3]|S /R/D 2 [4]|A /R/D/@d0 2 [v0]|A /R/D/@d1 2 [v1]|A /R/D/@d2 2 [v2]|A /R/D/@d3 2 [v3]|A /R/D/@d4 2 [v4]|A /R/D/@d5 2 [v5]|A /R/D/@d6 2 [v6]|E /R 1 [4]|S /R/E 2 [5]|A /R/E/@e0 2 [v0]|A /R/E/@e1 2 [v1]|E /R 1 [5]|E / 0 [5] END",
            ["limit"] = "S /R 1 []|S /R/A 2 [1]|A /R/A/@a0 2 [v0]|A /R/A/@a1 2 [v1]|A /R/A/@a2 2 [v2]|A /R/A/@a3 2 [v3]|A /R/A/@a4 2 [v4]|A /R/A/@a5 2 [v5]|A /R/A/@a6 2 [v6]|A /R/A/@a7 2 [v7]|E /R 1 [1]|S /R/B 2 [2]|A /R/B/@b0 2 [v0]|A /R/B/@b1 2 [v1]|A /R/B/@b2 2 [v2]|A /R/B/@b3 2 [v3]|A /R/B/@b4 2 [v4]|A /R/B/@b5 2 [v5]|A /R/B/@b6 2 [v6]|A /R/B/@b7 2 [v7]|A /R/B/@b8 2 [v8]|E /R 1 [2]|S /R/C 2 [3]|A /R/C/@c0 2 [v0]|A /R/C/@c1 2 [v1]|A /R/C/@c2 2 [v2]|A /R/C/@c3 2 [v3]|A /R/C/@c4 2 [v4]|A /R/C/@c5 2 [v5]|A /R/C/@c6 2 [v6]|A /R/C/@c7 2 [v7]|A /R/C/@c8 2 [v8]|A /R/C/@c9 2 [v9]|A /R/C/@c10 2 [v10]|A /R/C/@c11 2 [v11]|A /R/C/@c12 2 [v12]|A /R/C/@c13 2 [v13]|A /R/C/@c14 2 [v14]|A /R/C/@c15 2 [v15]|A /R/C/@c16 2 [v16]|E /R 1 [3]|S /R/D 2 [4]|A /R/D/@d0 2 [v0]|E /R 1 [4]|E / 0 [4] END",
            ["empty-with-attributes"] = "S /R 1 []|S /R/A 2 []|E /R 1 []|S /R/B 2 [t]|A /R/B/@y 2 [2]|E /R 1 [t]|S /R/C 2 []|A /R/C/@z 2 [3]|E /R 1 []|E / 0 [] END",
            ["whitespace"] = "N / 0 []|S /R 1 [\\n  ]|A /R/@a 1 [1]|A /R/@b 1 [2]|S /R/A 2 [x]|A /R/A/@c 2 [3]|E /R 1 [x]|E / 0 [x] END",
        };

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        [DynamicData(nameof(Documents))]
        public void ReadSurfacesAttributesAsBefore(string name, string xml)
        {
            string trace = Trace(xml);
            Assert.AreEqual(ExpectedTraces[name], trace, name);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void AttributeExpressionsMatchAsBefore()
        {
            var xml = "<R xmlns=\"urn:d\"><Item id=\"1\" type=\"a\"><Value>x</Value></Item><Item id=\"2\"><Value>y</Value></Item><Item><Value>z</Value></Item></R>";
            var found = new List<string>();
            using (var context = new XmlUnmarshallerContext(new MemoryStream(Encoding.UTF8.GetBytes(xml)), false, null))
            {
                while (context.Read())
                {
                    if (context.IsAttribute && context.TestExpression("@id", 2))
                        found.Add("id=" + StringUnmarshaller.Instance.Unmarshall(context));
                    else if (context.IsAttribute && context.TestExpression("Item/@type"))
                        found.Add("type=" + context.ReadText());
                    else if (context.IsStartElement && context.TestExpression("Value", 3))
                        found.Add("value=" + StringUnmarshaller.Instance.Unmarshall(context));
                }
            }
            CollectionAssert.AreEqual(new[] { "id=1", "type=a", "value=x", "id=2", "value=y", "value=z" }, found, string.Join(",", found));
        }

        public static IEnumerable<object[]> DuplicateDocuments => new[]
        {
            new object[] { "<R xmlns:a=\"urn:a\" xmlns:b=\"urn:b\"><E a:k=\"1\" b:k=\"2\">t</E></R>", "k", 3 },
            new object[] { "<R xmlns:a=\"urn:a\"><E k=\"1\" x=\"2\" a:k=\"3\">t</E></R>", "k", 2 },
            new object[] { "<R xmlns:a=\"urn:a\"><E" + Attrs("n", 7) + " a:n3=\"x\">t</E></R>", "n3", 2 },
            new object[] { "<R xmlns:a=\"urn:a\"><E" + Attrs("n", 8) + " a:n0=\"x\">t</E></R>", "n0", 2 },
            new object[] { "<R xmlns:a=\"urn:a\"><E" + Attrs("n", 12) + " a:n11=\"x\">t</E></R>", "n11", 2 },
            new object[] { "<R xmlns:k=\"urn:k\" xmlns:a=\"urn:a\" a:k=\"1\"><E>t</E></R>", "k", 0 },
        };

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        [DynamicData(nameof(DuplicateDocuments))]
        public void RepeatedLocalNameThrowsDictionaryArgumentException(string xml, string key, int readsBeforeThrow)
        {
            string expectedMessage;
            try
            {
                new Dictionary<string, string> { { key, "" } }.Add(key, "");
                throw new InvalidOperationException("unreachable");
            }
            catch (ArgumentException e)
            {
                expectedMessage = e.Message;
            }

            var reads = 0;
            using (var context = new XmlUnmarshallerContext(new MemoryStream(Encoding.UTF8.GetBytes(xml)), false, null))
            {
                var thrown = Assert.ThrowsExactly<ArgumentException>(() =>
                {
                    while (context.Read())
                        reads++;
                });
                Assert.AreEqual(expectedMessage, thrown.Message);
            }
            Assert.AreEqual(readsBeforeThrow, reads);
        }

        private static string Trace(string xml)
        {
            var sb = new StringBuilder();
            using (var context = new XmlUnmarshallerContext(new MemoryStream(Encoding.UTF8.GetBytes(xml)), false, null))
            {
                for (int i = 0; i < 400; i++)
                {
                    bool more = context.Read();
                    string kind = context.IsStartElement ? "S" : context.IsEndElement ? "E" : context.IsAttribute ? "A" : "N";
                    sb.Append(kind).Append(' ').Append(context.CurrentPath).Append(' ').Append(context.CurrentDepth)
                      .Append(" [").Append(context.ReadText()?.Replace("\n", "\\n").Replace("\t", "\\t")).Append(']');
                    if (!more)
                    {
                        sb.Append(" END");
                        break;
                    }
                    sb.Append('|');
                }
            }
            return sb.ToString();
        }
    }
}
