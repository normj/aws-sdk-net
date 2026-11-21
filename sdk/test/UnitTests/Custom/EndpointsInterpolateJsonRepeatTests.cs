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
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins Fn.InterpolateJson for templates used repeatedly (the generated providers pass the same literal on
    /// every request): every call must return exactly what the original JsonDocument + Utf8JsonWriter walk returns.
    /// </summary>
    [TestClass]
    public class EndpointsInterpolateJsonRepeatTests
    {
        // The original implementation, used as the reference.
        private static string Reference(string json, Dictionary<string, object> refs)
        {
            if (string.IsNullOrEmpty(json))
                return string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(json);
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    Walk(doc.RootElement, refs, writer);
                    writer.Flush();
                }
                return Encoding.UTF8.GetString(stream.ToArray());
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }

        private static void Walk(JsonElement element, Dictionary<string, object> refs, Utf8JsonWriter writer)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var p in element.EnumerateObject())
                    {
                        writer.WritePropertyName(p.Name);
                        Walk(p.Value, refs, writer);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                        Walk(item, refs, writer);
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

        private static Exception Capture(Func<string> f, out string result)
        {
            result = null;
            try
            {
                result = f();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private static void AssertParity(string template, Dictionary<string, object> refs)
        {
            var expectedEx = Capture(() => Reference(template, refs), out var expected);
            var actualEx = Capture(() => Fn.InterpolateJson(template, refs), out var actual);
            if (expectedEx != null)
            {
                Assert.IsNotNull(actualEx, $"expected {expectedEx.GetType()} for {template}");
                Assert.AreEqual(expectedEx.GetType(), actualEx.GetType(), template);
                Assert.AreEqual(expectedEx.Message, actualEx.Message, template);
            }
            else
            {
                Assert.IsNull(actualEx, $"unexpected {actualEx} for {template}");
                Assert.AreEqual(expected, actual, template);
            }
        }

        private static Dictionary<string, object> Refs(string region) => new Dictionary<string, object>
        {
            ["Region"] = region,
            ["Bucket"] = "my-bucket",
            ["UseFIPS"] = false,
            ["PartitionResult"] = Fn.Partition("us-west-2"),
            ["partitionResult"] = Fn.Partition("us-west-2"),
            ["bucketArn"] = Fn.ParseArn("arn:aws:s3:us-east-1:123456789012:accesspoint:ap"),
        };

        // Shapes of the attribute templates in the generated providers, plus edge cases.
        private static readonly string[] Templates =
        {
            @"{""authSchemes"":[{""disableDoubleEncoding"":true,""name"":""sigv4"",""signingName"":""s3"",""signingRegion"":""{Region}""}]}",
            @"{""authSchemes"":[{""name"":""sigv4"",""signingName"":""dynamodb"",""signingRegion"":""{Region}""}]}",
            @"{""authSchemes"":[{""disableDoubleEncoding"":true,""name"":""sigv4a"",""signingName"":""s3"",""signingRegionSet"":[""*""]},{""disableDoubleEncoding"":true,""name"":""sigv4"",""signingName"":""s3"",""signingRegion"":""{bucketArn#region}""}]}",
            @"{""authSchemes"":[{""name"":""sigv4"",""signingRegion"":""{PartitionResult#implicitGlobalRegion}""}],""backend"":""S3Express""}",
            @"{""authSchemes"":[{""name"":""sigv4"",""signingName"":""s3"",""signingRegion"":""us-east-1""}]}",
            @"{}",
            @"[]",
            @"""{Region}""",
            @"""plain""",
            @"42",
            @"null",
            @"true",
            @"{""a"":""{Region}"",""b"":""{Region}-{Bucket}"",""c"":""x{{y}}z"",""d"":""{{literal}}"",""n"":[1,-2.50,1e3,0.000001,12345678901234567890],""t"":true,""f"":false,""z"":null}",
            @"{""na\u00e9me"":""{Region}"",""q\""uote"":""va\\l\""ue\u0001"",""<tag>"":""&amp;'+`""}",
            @"  {  ""spaced""  :  [ ""{Region}"" , { ""k"" : ""{Bucket}"" } ]  }  ",
            @"{""deep"":{""a"":{""b"":{""c"":[[[""{Region}""]]]}}}}",
            @"{""emoji"":""\ud83d\ude00{Region}\u00e9""}",
            @"{""dup"":""{Region}"",""dup"":""{Bucket}""}",
            // Error cases: invalid JSON, unbalanced braces in values, unknown refs, bad GetAttr paths.
            @"{""a"":",
            @"{""a"":1}x",
            @"{""a"":""}""}",
            @"{""a"":""{Region""}",
            @"{""a"":""{Missing}""}",
            @"{""a"":""{bucketArn#resourceId[x]}""}",
            @"{""a"":""{Region#name}""}",
        };

        [ClassInitialize]
        public static void WarmGeneratedShapes(TestContext context)
        {
            // Use the generated template shapes before NonLiteralTemplatesMatchOriginal fills the capped cache.
            for (int i = 0; i < 5; i++)
            {
                Fn.InterpolateJson(Templates[i], Refs("us-west-2"));
            }
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void RepeatedTemplatesMatchOriginal()
        {
            var regions = new[] { "us-west-2", "eu-central-1", "us-gov-west-1" };
            for (int round = 0; round < 3; round++)
            {
                foreach (var template in Templates)
                {
                    foreach (var region in regions)
                    {
                        AssertParity(template, Refs(region));
                    }
                }
            }
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void EveryCharacterIsEscapedLikeTheWriter()
        {
            const string template = @"{""authSchemes"":[{""name"":""sigv4"",""signingRegion"":""{Region}"",""x"":""p{Region}s""}]}";
            var values = new List<string>();
            for (int c = 0; c < 0x300; c++)
            {
                values.Add(((char)c).ToString());
                values.Add("us-west-2" + (char)c + "a");
            }
            values.AddRange(new[] { "", "\ud83d\ude00", "\u2028\u2029", "\ufeff", "a\"b\\c", "\ud800", "x\udc00y", new string('a', 5000) + "\u00e9", new string('a', 5000), new string('a', 4096), new string('a', 4097) });

            for (int round = 0; round < 2; round++)
            {
                foreach (var value in values)
                {
                    AssertParity(template, Refs(value));
                }
            }
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void ErrorsAfterSuccessStillThrow()
        {
            const string template = @"{""a"":""{Region}"",""b"":""{Other}""}";
            var good = Refs("us-west-2");
            good["Other"] = "o";
            var missing = Refs("us-west-2");
            var nullValue = Refs("us-west-2");
            nullValue["Other"] = null;

            for (int i = 0; i < 3; i++)
            {
                AssertParity(template, good);
                AssertParity(template, missing);
                AssertParity(template, nullValue);
            }
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void NonLiteralTemplatesMatchOriginal()
        {
            for (int i = 0; i < 600; i++)
            {
                var template = "{\"i\":" + i + ",\"r\":\"{Region}\",\"s\":\"" + Guid.NewGuid().ToString("N") + "\"}";
                AssertParity(template, Refs("us-west-2"));
                AssertParity(template, Refs("eu-west-1"));
            }
            var longTemplate = "{\"long\":\"" + new string('a', 5000) + "{Region}\"}";
            AssertParity(longTemplate, Refs("us-west-2"));
            AssertParity(longTemplate, Refs("eu-west-1"));

            var type = typeof(Fn).Assembly.GetType("Amazon.Runtime.Internal.Endpoints.StandardLibrary.InterpolatedJsonTemplate");
            if (type != null)
            {
                var cache = (System.Collections.IDictionary)type.GetField("_cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
                Assert.IsFalse(cache.Contains(longTemplate), "over-length templates must not be kept alive by the cache");
            }
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void GeneratedTemplateShapesUseTheCachedPath()
        {
            // Guards that the parity tests above exercise the cached path, not only the original one.
            var type = typeof(Fn).Assembly.GetType("Amazon.Runtime.Internal.Endpoints.StandardLibrary.InterpolatedJsonTemplate");
            if (type == null)
                Assert.Inconclusive("No template cache in this build.");
            var cache = (System.Collections.IDictionary)type.GetField("_cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
            for (int i = 0; i < 5; i++)
            {
                Fn.InterpolateJson(Templates[i], Refs("us-west-2"));
                Assert.IsTrue(cache.Contains(Templates[i]), Templates[i]);
                Assert.IsNotNull(cache[Templates[i]], Templates[i]);
            }
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void EmptyTemplates()
        {
            Assert.AreEqual(string.Empty, Fn.InterpolateJson("", Refs("us-west-2")));
            Assert.AreEqual(string.Empty, Fn.InterpolateJson(null, Refs("us-west-2")));
            Assert.AreEqual(string.Empty, Fn.InterpolateJson(null, null));
        }

        [TestMethod]
        [TestCategory("Endpoints")]
        public void ConcurrentTemplates()
        {
            var regions = new[] { "us-west-2", "eu-central-1", "ap-south-1", "us-gov-west-1" };
            Parallel.For(0, 4000, i =>
            {
                var template = Templates[i % 5];
                AssertParity(template, Refs(regions[i % regions.Length]));
            });
        }
    }
}
