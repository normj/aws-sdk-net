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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins the attributes and headers that <see cref="Endpoint(string, string, string)"/> builds from JSON,
    /// and the exception it throws for bad input, against the original JsonDocument-based implementation.
    /// </summary>
    [TestClass]
    public class EndpointJsonParsingEquivalenceTests
    {
        private const string Url = "https://example.amazonaws.com";

        private static readonly string[] AttributeInputs =
        {
            // Shapes produced by the generated endpoint providers.
            @"{""authSchemes"":[{""disableDoubleEncoding"":true,""name"":""sigv4"",""signingName"":""s3"",""signingRegion"":""us-west-2""}]}",
            @"{""backend"":""S3Express"",""authSchemes"":[{""disableDoubleEncoding"":true,""name"":""sigv4-s3express"",""signingName"":""s3express"",""signingRegion"":""us-east-1""}]}",
            @"{""authSchemes"":[{""name"":""sigv4a"",""signingName"":""s3"",""signingRegionSet"":[""*""],""disableDoubleEncoding"":true},{""name"":""sigv4"",""signingName"":""s3"",""signingRegion"":""us-east-1"",""disableDoubleEncoding"":true}]}",
            @"{""metricValues"":[""O""]}",
            "{}",
            " { } ",
            "\t{\r\n  \"a\" : \"b\" ,\n \"c\" : [ ] }\n",
            // Numbers in every branch of the conversion chain.
            @"{""i"":0,""neg"":-5,""max32"":2147483647,""over32"":2147483648,""min32"":-2147483648,""under32"":-2147483649}",
            @"{""l"":9223372036854775807,""u"":18446744073709551615,""big"":18446744073709551616,""d"":1.5,""e"":1e3,""ne"":-2.5E-3,""z"":-0,""zf"":0.0}",
            @"{""huge"":1e308,""tiny"":5e-324}",
            // Nesting, arrays, mixed values.
            @"{""a"":{""b"":{""c"":[1,""two"",true,false,{""d"":[]},[[3]]]}}}",
            @"{""arr"":[[],[[]],{}]}",
            // Duplicate names keep the last value.
            @"{""x"":1,""x"":""second""}",
            @"{""x"":{""y"":1},""x"":[2]}",
            // Escapes in names and values.
            @"{""na\u006de"":""va\u006cue"",""q"":""\""quoted\"""",""bs"":""a\\b"",""u"":""\u00e9\u4e2d"",""sp"":""\uD83D\uDE00""}",
            "{\"raw\":\"\u00e9\u4e2d\U0001F600\"}",
            // Depth near the limit.
            Nested(63),
            Nested(64),
            Nested(65),
            // Values the original rejects after a successful parse.
            @"{""n"":null}",
            @"{""a"":[1,null]}",
            @"{""ok"":1,""bad"":null,""broken"":}",
            @"{""inf"":1e400}",
            @"{""ninf"":-1e400}",
            @"{""inf"":1e400,""broken"":}",
            // Roots other than an object.
            "[]",
            "[1,2]",
            "1",
            "\"s\"",
            "true",
            "null",
            "[1,}",
            // Malformed or trailing content.
            "   ",
            "{",
            "}",
            @"{""a"":1",
            @"{""a"":1,}",
            @"{""a"" 1}",
            @"{a:1}",
            @"{""a"":1} x",
            @"{""a"":1}{}",
            @"{""a"":1} // comment",
            @"{/*c*/""a"":1}",
            @"{""a"":'b'}",
            @"{""a"":01}",
            @"{""a"":""\x""}",
            @"{""a"":""line" + "\n" + @"break""}",
            // Escaped lone surrogates decode fine in the reader but fail in GetString.
            @"{""s"":""\uD800""}",
            @"{""\uDC00"":1}",
            @"{""s"":""\uD800"",""broken"":}",
            // Invalid UTF-16 in the input string itself.
            "{\"s\":\"\uD800\"}",
            "{\"s\":\"\uDC00x\"}",
        };

        private static readonly string[] HeaderInputs =
        {
            @"{""x-amz-test"":[""a""]}",
            @"{""x-amz-test"":[""a"",""b""],""other"":[]}",
            @"{""x-amz-test"":[""a"",null]}",
            "{}",
            " {\n} ",
            // Non-array values give an empty list.
            @"{""h"":""scalar"",""n"":1,""o"":{""a"":[1]},""t"":true,""nl"":null}",
            // Items the original cannot read as strings.
            @"{""h"":[1]}",
            @"{""h"":[true]}",
            @"{""h"":[[""a""]]}",
            @"{""h"":[{}]}",
            @"{""h"":[1],""broken"":}",
            // Duplicate names.
            @"{""h"":[""a""],""h"":[""b""]}",
            @"{""h"":1,""h"":[""b""]}",
            // Escapes.
            @"{""na\u006de"":[""va\u006cue"",""\u00e9""]}",
            @"{""h"":[""\uD800""]}",
            "{\"h\":[\"\uD800\"]}",
            // Roots other than an object, malformed, trailing.
            "[]",
            "\"s\"",
            "null",
            "   ",
            "{",
            @"{""h"":[""a""]",
            @"{""h"":[""a"",]}",
            @"{""h"":[""a""]} x",
            @"{""h"":[""a""]}[]",
            Nested(64),
            Nested(65),
        };

        [TestMethod]
        [TestCategory("Endpoints")]
        public void AttributesMatchJsonDocumentImplementation()
        {
            foreach (var json in AttributeInputs)
            {
                var expected = Capture(() => Describe(ReferenceAttributes(json)));
                var actual = Capture(() => Describe(new Endpoint(Url, json, null).Attributes));
                Assert.AreEqual(expected, actual, "attributes: " + json);
            }
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void HeadersMatchJsonDocumentImplementation()
        {
            foreach (var json in HeaderInputs)
            {
                var expected = Capture(() => Describe(ReferenceHeaders(json)));
                var actual = Capture(() => Describe(new Endpoint(Url, null, json).Headers));
                Assert.AreEqual(expected, actual, "headers: " + json);
            }
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void AttributesHeadersAndUrlTogether()
        {
            var endpoint = new Endpoint(Url,
                @"{""authSchemes"":[{""disableDoubleEncoding"":true,""name"":""sigv4"",""signingName"":""s3"",""signingRegion"":""us-west-2""}]}",
                @"{""x-amz-a"":[""1"",""2""]}");

            Assert.AreEqual(Url, endpoint.URL);
            var schemes = (List<object>)endpoint.Attributes["authSchemes"];
            Assert.AreEqual(1, schemes.Count);
            var scheme = (IPropertyBag)schemes[0];
            Assert.AreEqual(true, scheme["disableDoubleEncoding"]);
            Assert.AreEqual("sigv4", scheme["name"]);
            Assert.AreEqual("s3", scheme["signingName"]);
            Assert.AreEqual("us-west-2", scheme["signingRegion"]);
            Assert.IsNull(scheme["missing"]);
            CollectionAssert.AreEqual(new[] { "1", "2" }, (ICollection)endpoint.Headers["x-amz-a"]);
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void NumbersAreBoxedAsDouble()
        {
            // The original conversion is a conditional expression of type double, so integers come back as double.
            var endpoint = new Endpoint(Url, @"{""i"":1,""l"":9223372036854775807,""u"":18446744073709551615,""d"":2.5}", null);
            Assert.AreEqual(1d, endpoint.Attributes["i"]);
            Assert.AreEqual((double)long.MaxValue, endpoint.Attributes["l"]);
            Assert.AreEqual((double)ulong.MaxValue, endpoint.Attributes["u"]);
            Assert.AreEqual(2.5d, endpoint.Attributes["d"]);
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void EmptyAndNullJsonLeaveAttributesAndHeadersNull()
        {
            foreach (var json in new[] { null, "" })
            {
                var endpoint = new Endpoint(Url, json, json);
                Assert.IsNull(endpoint.Attributes);
                Assert.IsNull(endpoint.Headers);
            }
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void ResultsAreMutableAndIndependent()
        {
            const string json = @"{""a"":[1],""o"":{""k"":""v""}}";
            var first = new Endpoint(Url, json, @"{""h"":[""x""]}");
            var second = new Endpoint(Url, json, @"{""h"":[""x""]}");

            ((List<object>)first.Attributes["a"]).Add(2);
            ((IPropertyBag)first.Attributes["o"])["k"] = "changed";
            first.Attributes["new"] = 1;
            first.Headers["h"].Add("y");
            first.Headers["added"] = new List<string>();

            Assert.AreEqual(1, ((List<object>)second.Attributes["a"]).Count);
            Assert.AreEqual("v", ((IPropertyBag)second.Attributes["o"])["k"]);
            Assert.IsNull(second.Attributes["new"]);
            Assert.AreEqual(1, second.Headers["h"].Count);
            Assert.IsFalse(second.Headers.ContainsKey("added"));
        }

        // The attributes cache stores the tree the Utf8JsonReader parser built on a miss and hands out deep
        // copies on hits. Every input must match the original on the miss and on later hits, and a caller that
        // mutates a result (including one parsed on a miss) must not change what later calls get.
        [TestMethod]
        [TestCategory("Endpoints")]
        public void AttributesMatchJsonDocumentImplementationOnCacheMissAndHit()
        {
            var cacheField = typeof(Endpoint).GetField("_parsedAttributesCache", BindingFlags.Static | BindingFlags.NonPublic);
            var countField = typeof(Endpoint).GetField("_parsedAttributesCacheCount", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(cacheField);
            Assert.IsNotNull(countField);
            var cache = (IDictionary<string, PropertyBag>)cacheField.GetValue(null);

            foreach (var json in AttributeInputs)
            {
                // Start from an empty cache so the first call below is a miss and the next ones are hits.
                cache.Clear();
                countField.SetValue(null, 0);

                var expected = Capture(() => Describe(ReferenceAttributes(json)));
                var miss = Capture(() => MutateAndReturnOriginal(new Endpoint(Url, json, null).Attributes));
                Assert.AreEqual(expected, miss, "miss: " + json);
                if (!string.IsNullOrEmpty(json) && !expected.StartsWith("THROWS ", StringComparison.Ordinal) && json.Length <= 4096)
                {
                    Assert.IsTrue(cache.ContainsKey(json), "not cached: " + json);
                }
                else
                {
                    Assert.IsFalse(cache.ContainsKey(json), "cached: " + json);
                }

                for (int i = 0; i < 2; i++)
                {
                    var hit = Capture(() => MutateAndReturnOriginal(new Endpoint(Url, json, null).Attributes));
                    Assert.AreEqual(expected, hit, "hit " + i + ": " + json);
                }
            }
        }

        // Renders the bag, then changes every container in it, and returns the rendering from before the change.
        private static string MutateAndReturnOriginal(IPropertyBag bag)
        {
            var before = Describe(bag);
            Mutate(bag);
            return before;
        }

        private static void Mutate(object value)
        {
            if (value is PropertyBag bag)
            {
                var properties = (Dictionary<string, object>)PropertiesField.GetValue(bag);
                foreach (var child in new List<object>(properties.Values))
                {
                    Mutate(child);
                }
                bag["__mutated"] = "x";
            }
            else if (value is List<object> list)
            {
                foreach (var child in list.ToArray())
                {
                    Mutate(child);
                }
                list.Add("__mutated");
            }
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void LargeDocumentMatches()
        {
            var sb = new StringBuilder("{");
            for (int i = 0; i < 500; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("\"key").Append(i).Append("\":[\"").Append(new string('v', i)).Append("\",").Append(i).Append(",{\"n\":").Append((i * 1.5).ToString(CultureInfo.InvariantCulture)).Append("}]");
            }
            sb.Append('}');
            var json = sb.ToString();

            Assert.AreEqual(Describe(ReferenceAttributes(json)), Describe(new Endpoint(Url, json, null).Attributes));
        }

        private static string Nested(int depth)
        {
            // depth counts the object and array levels, including the root object.
            var sb = new StringBuilder("{\"a\":");
            for (int i = 1; i < depth; i++) sb.Append('[');
            sb.Append('1');
            for (int i = 1; i < depth; i++) sb.Append(']');
            sb.Append('}');
            return sb.ToString();
        }

        // The original Endpoint(string, string, string) attributes code.
        private static IPropertyBag ReferenceAttributes(string attributesJson)
        {
            using JsonDocument doc = JsonDocument.Parse(attributesJson);
            var attributes = doc.RootElement;
            return PropertyBag.FromJsonElement(attributes);
        }

        // The original Endpoint(string, string, string) headers code.
        private static IDictionary<string, IList<string>> ReferenceHeaders(string headersJson)
        {
            using JsonDocument doc = JsonDocument.Parse(headersJson);
            var headers = doc.RootElement;
            var result = new Dictionary<string, IList<string>>();
            foreach (JsonProperty key in headers.EnumerateObject())
            {
                var headerValues = new List<string>();
                if (headers.TryGetProperty(key.Name, out JsonElement value) && value.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in value.EnumerateArray())
                    {
                        headerValues.Add(item.GetString());
                    }
                }
                result.Add(key.Name, headerValues);
            }
            return result;
        }

        private static string Capture(Func<string> action)
        {
            try
            {
                return action();
            }
            catch (Exception e)
            {
                // The exact exception type and message must match too.
                return "THROWS " + e.GetType().FullName + ": " + e.Message;
            }
        }

        private static readonly FieldInfo PropertiesField =
            typeof(PropertyBag).GetField("properties", BindingFlags.Instance | BindingFlags.NonPublic);

        // Renders a value tree with types and in enumeration order, so any difference in shape,
        // value, numeric type or ordering shows up.
        private static string Describe(object value)
        {
            var sb = new StringBuilder();
            Describe(value, sb);
            return sb.ToString();
        }

        private static void Describe(object value, StringBuilder sb)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case PropertyBag bag:
                    sb.Append("bag{");
                    foreach (var kvp in (Dictionary<string, object>)PropertiesField.GetValue(bag))
                    {
                        sb.Append(Escape(kvp.Key)).Append('=');
                        Describe(kvp.Value, sb);
                        sb.Append(';');
                    }
                    sb.Append('}');
                    break;
                case IDictionary<string, IList<string>> headers:
                    sb.Append(headers.GetType().Name).Append('{');
                    foreach (var kvp in headers)
                    {
                        sb.Append(Escape(kvp.Key)).Append('=');
                        Describe(kvp.Value, sb);
                        sb.Append(';');
                    }
                    sb.Append('}');
                    break;
                case string s:
                    sb.Append(Escape(s));
                    break;
                case IEnumerable list:
                    sb.Append(value.GetType().Name).Append('[');
                    foreach (var item in list)
                    {
                        Describe(item, sb);
                        sb.Append(',');
                    }
                    sb.Append(']');
                    break;
                case IFormattable f:
                    sb.Append(value.GetType().Name).Append(':').Append(f.ToString(value is double ? "R" : null, CultureInfo.InvariantCulture));
                    break;
                default:
                    sb.Append(value.GetType().Name).Append(':').Append(value);
                    break;
            }
        }

        private static string Escape(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s)
            {
                if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
