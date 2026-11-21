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
    /// Pins XmlUnmarshallerContext.TestExpression(string, int) to the algorithm it had before the
    /// early '/' check: at every node of several documents (elements, empty elements, attributes,
    /// end elements), for every element and attribute name in the document, case variants, prefixed
    /// paths, attribute paths and edge cases, at every depth from 0 to 7.
    /// </summary>
    [TestClass]
    public class XmlUnmarshallerContextTestExpressionTests
    {
        private static readonly string[] Documents =
        {
            "<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Name>bucket</Name><Contents><Key>a/b.txt</Key><LastModified>2024-01-01T00:00:00.000Z</LastModified><ETag>&quot;x&quot;</ETag><Size>5</Size><StorageClass>STANDARD</StorageClass><Owner><ID>id</ID><DisplayName>d</DisplayName></Owner></Contents><Contents><Key/><Size>0</Size></Contents><IsTruncated>false</IsTruncated></ListBucketResult>",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Error><Type>Sender</Type><Code>NoSuchKey</Code><Message>The key does not exist</Message><RequestId>r</RequestId></Error>",
            "<DescribeInstancesResponse xmlns=\"http://ec2.amazonaws.com/doc/2016-11-15/\"><requestId>r</requestId><reservationSet><item><reservationId>r-1</reservationId><instancesSet><item><instanceId>i-1</instanceId><tagSet><item><key>Name</key><value>v</value></item></tagSet></item></instancesSet></item></reservationSet></DescribeInstancesResponse>",
            "<Root a=\"1\" Bb=\"2\" ccc=\"3\"><Child x=\"y\"><member>m</member><Member>M</Member><key>k</key><KEY>K</KEY></Child><Child/><caf\u00e9>c</caf\u00e9><Stra\u00dfe>s</Stra\u00dfe><\u0130d>i</\u0130d><e x=\"1\"/></Root>",
            "<A><B><C><D><E><F><G>deep</G></F></E></D></C></B></A>",
        };

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void TestExpressionMatchesOriginalAlgorithm()
        {
            long checks = 0, matches = 0;
            foreach (var document in Documents)
            {
                var expressions = Expressions(document);
                using (var context = new XmlUnmarshallerContext(new MemoryStream(Encoding.UTF8.GetBytes(document)), false, null))
                {
                    do
                    {
                        foreach (var expression in expressions)
                        {
                            for (int depth = 0; depth <= 7; depth++)
                            {
                                bool expected = OriginalTestExpression(context, expression, depth);
                                bool actual = context.TestExpression(expression, depth);
                                Assert.AreEqual(expected, actual, $"'{expression}' at depth {depth}, path '{context.CurrentPath}', document {document}");
                                checks++;
                                if (actual) matches++;
                            }
                        }
                    }
                    while (context.Read());
                }
            }

            Assert.IsTrue(checks > 50000, checks.ToString());
            Assert.IsTrue(matches > 500, matches.ToString());
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void TestExpressionWithNullThrowsNullReferenceException()
        {
            using (var context = new XmlUnmarshallerContext(new MemoryStream(Encoding.UTF8.GetBytes(Documents[0])), false, null))
            {
                Assert.ThrowsExactly<NullReferenceException>(() => context.TestExpression(null, 0));
                context.Read();
                Assert.ThrowsExactly<NullReferenceException>(() => context.TestExpression(null, 1));
            }
        }

        private static List<string> Expressions(string document)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var attributes = new HashSet<string>(StringComparer.Ordinal);
            using (var reader = System.Xml.XmlReader.Create(new StringReader(document)))
            {
                while (reader.Read())
                {
                    if (reader.NodeType != System.Xml.XmlNodeType.Element)
                        continue;
                    names.Add(reader.LocalName);
                    while (reader.MoveToNextAttribute())
                        attributes.Add(reader.LocalName);
                    reader.MoveToElement();
                }
            }

            var expressions = new List<string> { ".", "", "/", "//", "@", "member/", "/Key", "x", "ey", "Contents/Ke", "K\u00e9y" };
            foreach (var name in names)
            {
                expressions.Add(name);
                expressions.Add(name.ToUpperInvariant());
                expressions.Add(name.ToLowerInvariant());
                expressions.Add(name.Substring(1));
                expressions.Add(name + "x");
                expressions.Add("/" + name);
                foreach (var parent in names)
                {
                    expressions.Add(parent + "/" + name);
                }
                foreach (var attribute in attributes)
                {
                    expressions.Add(name + "/@" + attribute);
                }
            }
            foreach (var attribute in attributes)
            {
                expressions.Add("@" + attribute);
                expressions.Add("@" + attribute.ToUpperInvariant());
                expressions.Add(attribute);
            }
            return expressions.Distinct(StringComparer.Ordinal).ToList();
        }

        // XmlUnmarshallerContext.TestExpression(string, int) as it was before the early '/' check.
        private static bool OriginalTestExpression(XmlUnmarshallerContext context, string expression, int startingStackDepth)
        {
            if (expression.Equals("."))
                return true;

            int requiredDepth = startingStackDepth;
            for (int i = expression.IndexOf('/'); i > -1; i = expression.IndexOf('/', i + 1))
            {
                if (expression[0] != '@')
                    requiredDepth++;
            }

            if (requiredDepth != context.CurrentDepth)
                return false;

            // The element path, or "{elementPath}/@{attribute}" on an attribute. CurrentPath is "/" for an
            // empty element path, where the original compared against an empty span.
            string path = context.IsAttribute || context.CurrentDepth > 0 ? context.CurrentPath : string.Empty;

            return path.Length > expression.Length
                && path[path.Length - expression.Length - 1] == '/'
                && path.AsSpan().EndsWith(expression.AsSpan(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
