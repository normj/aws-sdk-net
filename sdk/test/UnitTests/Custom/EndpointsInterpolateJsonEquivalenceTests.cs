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
using Amazon.Runtime.Endpoints;
using Amazon.Runtime.Internal.Endpoints.StandardLibrary;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins Fn.InterpolateJson to the output of the original JsonDocument based implementation, which is
    /// kept below as a reference.
    /// </summary>
    [TestClass]
    public class EndpointsInterpolateJsonEquivalenceTests
    {
        // Shapes taken from the generated endpoint providers, plus edge cases.
        private static readonly string[] Templates =
        {
            "{\"authSchemes\":[{\"disableDoubleEncoding\":true,\"name\":\"sigv4\",\"signingName\":\"s3\",\"signingRegion\":\"{Region}\"}]}",
            "{\"authSchemes\":[{\"disableDoubleEncoding\":true,\"name\":\"sigv4\",\"signingName\":\"s3\",\"signingRegion\":\"us-east-1\"}]}",
            "{\"authSchemes\":[{\"disableDoubleEncoding\":true,\"name\":\"sigv4a\",\"signingName\":\"s3\",\"signingRegionSet\":[\"*\"]},{\"disableDoubleEncoding\":true,\"name\":\"sigv4\",\"signingName\":\"s3\",\"signingRegion\":\"{bucketArn#region}\"}]}",
            "{\"backend\":\"S3Express\",\"authSchemes\":[{\"disableDoubleEncoding\":true,\"name\":\"sigv4-s3express\",\"signingName\":\"s3express\",\"signingRegion\":\"{Region}\"}]}",
            "{\n    \"authSchemes\": [\n        {\n            \"name\": \"sigv4\",\n            \"signingName\": \"dynamodb\",\n            \"signingRegion\": \"us-east-1\"\n        }\n    ]\n}",
            "{\"authSchemes\":[{\"name\":\"sigv4\",\"signingRegion\":\"{PartitionResult#implicitGlobalRegion}\"}]}",
            "{\"metricValues\":[\"O\"],\"authSchemes\":[]}",
            "{}",
            "[]",
            "\"{Region}\"",
            "\"plain\"",
            "42",
            "true",
            "null",
            "{\"n\":1.50e+3,\"m\":-0,\"i\":12345678901234567890,\"d\":0.1}",
            "{\"t\":true,\"f\":false,\"z\":null,\"a\":[1,[2,[3,{}]],\"x\"]}",
            "{\"esc\":\"a\\\"b\\\\c\\/d\\n\\u00e9\\u007bRegion\\u007d\"}",
            "{\"\\u0061key\":\"v\",\"<html>\":\"&'\",\"caf\u00e9\":\"\u00e9\u4e2d\ud83d\ude00\"}",
            "{\"dup\":\"1\",\"dup\":\"{Region}\"}",
            "{\"lit\":\"{{literal}} and {Region}\"}",
            "   {\"spaces\" :  [ \"a\" , \"b\" ]  }   ",
            // malformed: the original returns an empty string
            "{",
            "{\"a\":}",
            "{\"a\":1,}",
            "{\"a\":1} {\"b\":2}",
            "{\"a\":1} x",
            "{\"a\":\"{Region}\",",
            "// comment\n{}",
            "   ",
            "{'a':1}",
            "{\"a\":\"{Region}\"} trailing",
        };

        private static Dictionary<string, object> CreateRefs()
        {
            var arn = new PropertyBag();
            arn["region"] = "eu-west-1";
            return new Dictionary<string, object>
            {
                ["Region"] = "us-west-2",
                ["bucketArn"] = arn,
                ["PartitionResult"] = Fn.Partition("us-west-2"),
            };
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        [TestCategory("StandardLibrary")]
        public void MatchesReference()
        {
            var refs = CreateRefs();
            foreach (var template in Templates)
            {
                AssertEquivalent(template, refs);
            }
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        [TestCategory("StandardLibrary")]
        public void KnownOutput()
        {
            Assert.AreEqual(
                "{\"authSchemes\":[{\"disableDoubleEncoding\":true,\"name\":\"sigv4\",\"signingName\":\"s3\",\"signingRegion\":\"us-west-2\"}]}",
                Fn.InterpolateJson(Templates[0], CreateRefs()));
            Assert.AreEqual(string.Empty, Fn.InterpolateJson(null, null));
            Assert.AreEqual(string.Empty, Fn.InterpolateJson("", null));
            Assert.AreEqual(string.Empty, Fn.InterpolateJson("{", null));
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        [TestCategory("StandardLibrary")]
        public void MalformedJsonIsRejectedBeforeAnyTemplateIsEvaluated()
        {
            // The missing ref would throw KeyNotFoundException, but the JSON is invalid, so the
            // original implementation returned an empty string without evaluating it.
            Assert.AreEqual(string.Empty, Fn.InterpolateJson("{\"a\":\"{Missing}\",", new Dictionary<string, object>()));
            Assert.AreEqual(string.Empty, Fn.InterpolateJson("{\"a\":\"{Missing}\"} x", new Dictionary<string, object>()));
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        [TestCategory("StandardLibrary")]
        public void TemplateErrorsPropagate()
        {
            Assert.ThrowsExactly<KeyNotFoundException>(() => Fn.InterpolateJson("{\"a\":\"{Missing}\"}", new Dictionary<string, object>()));
            Assert.ThrowsExactly<ArgumentException>(() => Fn.InterpolateJson("{\"a\":\"{Region\"}", CreateRefs()));
            Assert.ThrowsExactly<NullReferenceException>(() => Fn.InterpolateJson("{\"a\":\"{Region}\"}", null));
            // A failed call does not break the next one on the same thread.
            Assert.AreEqual("{\"a\":\"us-west-2\"}", Fn.InterpolateJson("{\"a\":\"{Region}\"}", CreateRefs()));
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        [TestCategory("StandardLibrary")]
        public void InvalidUtf16BehavesLikeReference()
        {
            AssertEquivalent("{\"a\":\"\ud800\"}", CreateRefs());
            AssertEquivalent("{\"\udc00\":1}", CreateRefs());
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        [TestCategory("StandardLibrary")]
        public void LargeAndDeepDocuments()
        {
            var refs = CreateRefs();
            var sb = new StringBuilder("{\"authSchemes\":[");
            for (int i = 0; i < 400; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":\"sigv4\",\"i\":").Append(i).Append(",\"signingRegion\":\"{Region}\"}");
            }
            sb.Append("]}");
            AssertEquivalent(sb.ToString(), refs);

            foreach (var depth in new[] { 63, 64, 65 })
            {
                AssertEquivalent(new string('[', depth) + new string(']', depth), refs);
            }
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        [TestCategory("StandardLibrary")]
        public void ReentrantCallFromToString()
        {
            var refs = CreateRefs();
            refs["nested"] = new Reentrant(refs);
            AssertEquivalent("{\"outer\":\"{nested}\",\"after\":\"{Region}\"}", refs);
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        [TestCategory("StandardLibrary")]
        public void ConcurrentCalls()
        {
            var expected = ReferenceInterpolateJson(Templates[2], CreateRefs());
            Parallel.For(0, 2000, i =>
            {
                var refs = CreateRefs();
                Assert.AreEqual(expected, Fn.InterpolateJson(Templates[2], refs));
                Assert.AreEqual(string.Empty, Fn.InterpolateJson("{", refs));
            });
        }

        private sealed class Reentrant
        {
            private readonly Dictionary<string, object> _refs;
            public Reentrant(Dictionary<string, object> refs) { _refs = refs; }
            public override string ToString() => Fn.InterpolateJson("{\"inner\":\"{Region}\"}", _refs);
        }

        private static void AssertEquivalent(string template, Dictionary<string, object> refs)
        {
            string expected = null, actual = null;
            Exception expectedException = null, actualException = null;
            try { expected = ReferenceInterpolateJson(template, refs); } catch (Exception e) { expectedException = e; }
            try { actual = Fn.InterpolateJson(template, refs); } catch (Exception e) { actualException = e; }

            if (expectedException != null)
            {
                Assert.IsNotNull(actualException, "expected " + expectedException.GetType().Name + " for: " + template);
                Assert.AreEqual(expectedException.GetType(), actualException.GetType(), template);
                Assert.AreEqual(expectedException.Message, actualException.Message, template);
            }
            else
            {
                Assert.IsNull(actualException, "unexpected " + actualException?.GetType().Name + " " + actualException?.Message + " for: " + template);
                Assert.AreEqual(expected, actual, template);
            }
        }

        // The original implementation (its .NET Framework branch, which writes the same bytes).
        private static string ReferenceInterpolateJson(string json, Dictionary<string, object> refs)
        {
            if (string.IsNullOrEmpty(json))
                return string.Empty;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                using var stream = new MemoryStream();
                using var writer = new Utf8JsonWriter(stream);
                ReferenceInterpolateJson(doc.RootElement, refs, writer);
                writer.Flush();
                return Encoding.UTF8.GetString(stream.ToArray());
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }

        private static void ReferenceInterpolateJson(JsonElement element, Dictionary<string, object> refs, Utf8JsonWriter writer)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject())
                    {
                        writer.WritePropertyName(property.Name);
                        ReferenceInterpolateJson(property.Value, refs, writer);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        ReferenceInterpolateJson(item, refs, writer);
                    }
                    writer.WriteEndArray();
                    break;
                case JsonValueKind.String:
                    writer.WriteStringValue(Fn.Interpolate(element.GetString(), refs));
                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }
    }
}
