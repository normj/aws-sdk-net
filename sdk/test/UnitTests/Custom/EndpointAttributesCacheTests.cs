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
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins the behavior of Endpoint(url, attributesJson, headersJson) for repeated attribute JSON:
    /// every call must return an independent object tree identical to a fresh JsonDocument parse.
    /// </summary>
    [TestClass]
    public class EndpointAttributesCacheTests
    {
        private const string Url = "https://bucket.s3.us-west-2.amazonaws.com";

        private static string UniqueSuffix() => Guid.NewGuid().ToString("N");

        // The original implementation, used as the reference.
        private static object Reference(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String: return element.GetString();
                case JsonValueKind.Number:
                    // The original conditional expression has type double, so every number is boxed as double.
                    return element.TryGetInt32(out var i) ? i :
                           element.TryGetInt64(out var l) ? l :
                           element.TryGetUInt32(out var ui) ? ui :
                           element.TryGetUInt64(out var ul) ? ul :
                           element.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Object:
                    var d = new List<KeyValuePair<string, object>>();
                    var seen = new Dictionary<string, int>();
                    foreach (var p in element.EnumerateObject())
                    {
                        var v = Reference(p.Value);
                        if (seen.TryGetValue(p.Name, out var idx))
                        {
                            d[idx] = new KeyValuePair<string, object>(p.Name, v);
                        }
                        else
                        {
                            seen[p.Name] = d.Count;
                            d.Add(new KeyValuePair<string, object>(p.Name, v));
                        }
                    }
                    return d;
                case JsonValueKind.Array:
                    return element.EnumerateArray().Select(Reference).ToList();
                default:
                    throw new ArgumentException("Unsupported JSON value type.");
            }
        }

        private static List<KeyValuePair<string, object>> Entries(IPropertyBag bag)
        {
            // PropertyBag has no public enumeration; read its private dictionary to check order and contents.
            var field = typeof(PropertyBag).GetField("properties", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var dict = (Dictionary<string, object>)field.GetValue(bag);
            return dict.ToList();
        }

        private static void AssertSame(object expected, object actual)
        {
            if (expected is List<KeyValuePair<string, object>> expectedEntries)
            {
                Assert.IsInstanceOfType(actual, typeof(PropertyBag));
                var actualEntries = Entries((PropertyBag)actual);
                CollectionAssert.AreEqual(expectedEntries.Select(e => e.Key).ToList(), actualEntries.Select(e => e.Key).ToList());
                for (int k = 0; k < expectedEntries.Count; k++)
                {
                    AssertSame(expectedEntries[k].Value, actualEntries[k].Value);
                }
            }
            else if (expected is List<object> expectedList)
            {
                Assert.IsInstanceOfType(actual, typeof(List<object>));
                var actualList = (List<object>)actual;
                Assert.AreEqual(expectedList.Count, actualList.Count);
                for (int k = 0; k < expectedList.Count; k++)
                {
                    AssertSame(expectedList[k], actualList[k]);
                }
            }
            else
            {
                Assert.IsNotNull(actual);
                Assert.AreEqual(expected.GetType(), actual.GetType());
                Assert.AreEqual(expected, actual);
            }
        }

        private static void AssertMatchesReference(string json, IPropertyBag actual)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                AssertSame(Reference(doc.RootElement), actual);
            }
        }

        private static IEnumerable<object> AllContainers(object value)
        {
            if (value is PropertyBag bag)
            {
                yield return bag;
                foreach (var e in Entries(bag))
                    foreach (var c in AllContainers(e.Value)) yield return c;
            }
            else if (value is List<object> list)
            {
                yield return list;
                foreach (var item in list)
                    foreach (var c in AllContainers(item)) yield return c;
            }
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void RepeatedAttributesMatchFreshParse()
        {
            var json = "{\"authSchemes\":[{\"disableDoubleEncoding\":true,\"name\":\"sigv4\",\"signingName\":\"s3\",\"signingRegion\":\"us-west-2-" + UniqueSuffix() + "\"}]}";
            for (int i = 0; i < 3; i++)
            {
                var endpoint = new Endpoint(Url, json, null);
                AssertMatchesReference(json, endpoint.Attributes);
            }
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void RepeatedAttributesMatchFreshParse_AllValueKinds()
        {
            var json = "{\"s\":\"" + UniqueSuffix() + "\",\"i\":42,\"neg\":-7,\"big\":9223372036854775807,\"ubig\":18446744073709551615,\"d\":1.5,\"e\":1e300," +
                       "\"t\":true,\"f\":false,\"empty\":{},\"emptyList\":[],\"nested\":{\"a\":[1,[2,{\"b\":\"c\"}],\"x\"],\"z\":{\"y\":{}}}," +
                       "\"dup\":1,\"order\":2,\"dup\":\"second\",\"esc\":\"a\\u00e9\\\"\\n\"}";
            for (int i = 0; i < 3; i++)
            {
                var endpoint = new Endpoint(Url, json, null);
                AssertMatchesReference(json, endpoint.Attributes);
                Assert.IsInstanceOfType(endpoint.Attributes["i"], typeof(double));
                Assert.AreEqual("second", endpoint.Attributes["dup"]);
            }
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void RepeatedAttributesReturnIndependentTrees()
        {
            var json = "{\"authSchemes\":[{\"name\":\"sigv4\",\"signingRegion\":\"" + UniqueSuffix() + "\"},{\"name\":\"sigv4a\",\"signingRegionSet\":[\"*\"]}],\"backend\":\"S3Express\"}";

            var first = new Endpoint(Url, json, null);
            var second = new Endpoint(Url, json, null);

            Assert.AreNotSame(first.Attributes, second.Attributes);
            var firstContainers = AllContainers(first.Attributes).ToList();
            var secondContainers = AllContainers(second.Attributes).ToList();
            Assert.AreEqual(firstContainers.Count, secondContainers.Count);
            foreach (var c in firstContainers)
            {
                Assert.IsFalse(secondContainers.Any(o => ReferenceEquals(o, c)), "A container is shared between two endpoints.");
            }

            // Mutating one result must not leak into later results.
            first.Attributes["backend"] = "changed";
            first.Attributes["added"] = "x";
            var schemes = (List<object>)first.Attributes["authSchemes"];
            ((PropertyBag)schemes[0])["name"] = "mutated";
            ((List<object>)((PropertyBag)schemes[1])["signingRegionSet"]).Add("us-east-1");
            schemes.Add("extra");

            var third = new Endpoint(Url, json, null);
            AssertMatchesReference(json, third.Attributes);
            AssertMatchesReference(json, second.Attributes);
            Assert.IsNull(third.Attributes["added"]);
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void InvalidAttributesThrowOnEveryCall()
        {
            var invalid = new[]
            {
                ("{\"a\":", typeof(JsonException)),
                ("{\"a\":1}" + "x" + UniqueSuffix(), typeof(JsonException)),
                ("[1,2]", typeof(InvalidOperationException)),
                ("\"str" + UniqueSuffix() + "\"", typeof(InvalidOperationException)),
                ("{\"a\":null,\"u\":\"" + UniqueSuffix() + "\"}", typeof(ArgumentException)),
                ("{\"a\":[1,null],\"u\":\"" + UniqueSuffix() + "\"}", typeof(ArgumentException)),
            };

            foreach (var (json, expected) in invalid)
            {
                for (int i = 0; i < 2; i++)
                {
                    Exception caught = null;
                    try
                    {
                        new Endpoint(Url, json, null);
                    }
                    catch (Exception ex)
                    {
                        caught = ex;
                    }
                    Assert.IsNotNull(caught, json);
                    Assert.IsTrue(expected.IsAssignableFrom(caught.GetType()), json + " threw " + caught.GetType());
                }
            }
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void EmptyAndNullAttributesLeaveAttributesNull()
        {
            Assert.IsNull(new Endpoint(Url, null, null).Attributes);
            Assert.IsNull(new Endpoint(Url, "", "").Attributes);
            Assert.IsNull(new Endpoint(Url, "", "").Headers);
            Assert.ThrowsExactly<ArgumentNullException>(() => new Endpoint(null, "{}", null));
            Assert.ThrowsExactly<ArgumentNullException>(() => new Endpoint("", "{}", null));
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void ManyDistinctAndLongAttributesStillParse()
        {
            // More distinct strings than the cache holds, and strings longer than it accepts.
            for (int i = 0; i < 600; i++)
            {
                var json = "{\"n\":" + i + ",\"u\":\"" + UniqueSuffix() + "\"}";
                AssertMatchesReference(json, new Endpoint(Url, json, null).Attributes);
                AssertMatchesReference(json, new Endpoint(Url, json, null).Attributes);
            }

            var longJson = "{\"long\":\"" + new string('a', 10000) + UniqueSuffix() + "\"}";
            for (int i = 0; i < 2; i++)
            {
                AssertMatchesReference(longJson, new Endpoint(Url, longJson, null).Attributes);
            }
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void ConcurrentAttributesParse()
        {
            var jsons = Enumerable.Range(0, 8)
                .Select(i => "{\"authSchemes\":[{\"name\":\"sigv4\",\"signingRegion\":\"r" + i + UniqueSuffix() + "\"}]}")
                .ToArray();

            Parallel.For(0, 4000, i =>
            {
                var json = jsons[i % jsons.Length];
                var endpoint = new Endpoint(Url, json, null);
                AssertMatchesReference(json, endpoint.Attributes);
                endpoint.Attributes["authSchemes"] = null;
            });
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void HeadersAreUnchanged()
        {
            var endpoint = new Endpoint(Url, "{\"a\":\"" + UniqueSuffix() + "\"}", "{\"x-amz-h\":[\"v1\",\"v2\"],\"x-empty\":[],\"x-nonarray\":\"s\"}");
            CollectionAssert.AreEqual(new[] { "v1", "v2" }, endpoint.Headers["x-amz-h"].ToArray());
            Assert.AreEqual(0, endpoint.Headers["x-empty"].Count);
            Assert.AreEqual(0, endpoint.Headers["x-nonarray"].Count);
        }
    }
}
