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
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins <see cref="Fn.Interpolate"/> against a copy of its original single-pass implementation:
    /// results, exception types and messages, on the first call for a template and on repeat calls.
    /// </summary>
    [TestClass]
    public class EndpointsInterpolateTemplateTests
    {
        private class ToStringNull
        {
            public override string ToString() => null;
        }

        private static Dictionary<string, object> CreateRefs()
        {
            var partition = Fn.Partition("us-west-2");
            var nested = new PropertyBag();
            nested["inner"] = "deep";
            nested["list"] = new List<object> { "l0", "l1" };
            var bag = new PropertyBag();
            bag["nested"] = nested;
            bag["number"] = 42;
            bag["missing"] = null;
            bag["items"] = new List<object> { "i0", "i1", "i2" };

            return new Dictionary<string, object>
            {
                ["Bucket"] = "my-application-bucket",
                ["Region"] = "us-west-2",
                ["partitionResult"] = partition,
                ["bag"] = bag,
                ["arn"] = Fn.ParseArn("arn:aws:s3:us-west-2:123456789012:accesspoint:my-endpoint"),
                ["flag"] = true,
                ["count"] = 7,
                ["nullValue"] = null,
                ["toStringNull"] = new ToStringNull(),
                ["list"] = new List<object> { "a", "b" },
                ["text"] = "plain",
                [""] = "empty-key",
                ["with space"] = "spaced",
                ["a.b"] = "dotted-key",
            };
        }

        private static readonly string[] Templates =
        {
            "",
            "plain text with no placeholders",
            "https://{Bucket}.s3.{Region}.{partitionResult#dnsSuffix}",
            "https://dynamodb.{Region}.{partitionResult#dnsSuffix}",
            "https://s3-fips.dualstack.{Region}.{partitionResult#dualStackDnsSuffix}",
            "{Region}",
            "{partitionResult#name}",
            "{Bucket}{Region}",
            "prefix-{Region}-suffix",
            "{{literal}}",
            "{{{Region}}}",
            "{{",
            "}}",
            "a{{b}}c{Region}d{{e",
            "{bag#nested.inner}",
            "{bag#nested.list[1]}",
            "{bag#items[2]}",
            "{bag#items[5]}",
            "{bag#number}",
            "{arn#resourceId[1]}",
            "{arn#region}",
            "{arn#service}",
            "{partitionResult#name#ignored}",
            "{partitionResult#dnsSuffix#a#b}",
            "{flag}",
            "{count}",
            "{text}",
            "{}",
            "{with space}",
            "{a.b}",
            "{list#[0]}",
            "{list#[1]}",
            "{list#[9]}",
            // Missing refs and members
            "{doesNotExist}",
            "ok {Region} then {doesNotExist}",
            "{partitionResult#doesNotExist}",
            "{bag#missing}",
            "{bag#nested.doesNotExist}",
            "{nullValue}",
            "{nullValue#name}",
            "{toStringNull}",
            "x{toStringNull}y",
            // GetAttr on things that are not property bags
            "{text#length}",
            "{flag#name}",
            "{text#a.b}",
            "{text#[0]}",
            "{count#[0]}",
            "{partitionResult#}",
            "{partitionResult##}",
            "{#name}",
            "{bag#nested.}",
            "{bag#.inner}",
            "{bag#items[x]}",
            "{bag#items[]}",
            "{bag#items[-1]}",
            // Malformed templates
            "{",
            "}",
            "abc}",
            "abc}def",
            "{Region",
            "abc{Region",
            "{Region}}",
            "{Region}{",
            "{doesNotExist}{",
            "{Region}}x",
            "}{Region}",
            "{{Region}",
            "{Region}}}",
            "{Reg{ion}",
            "{a{b}c}",
        };

        [TestMethod]
        public void MatchesOriginalImplementation()
        {
            var refs = CreateRefs();
            foreach (var literal in Templates)
            {
                // A copy that is not interned, so the first call parses it and later calls use the cache.
                var template = new string(literal.ToCharArray());
                var expected = Capture(() => OriginalInterpolate(template, refs));
                for (int call = 0; call < 3; call++)
                {
                    var actual = Capture(() => Fn.Interpolate(template, refs));
                    Assert.AreEqual(expected, actual, $"template \"{literal}\", call {call}");
                }
            }
        }

        [TestMethod]
        public void LiteralTemplatesMatchOriginalImplementation()
        {
            var refs = CreateRefs();
            foreach (var template in Templates)
            {
                var expected = Capture(() => OriginalInterpolate(template, refs));
                Assert.AreEqual(expected, Capture(() => Fn.Interpolate(template, refs)), template);
                Assert.AreEqual(expected, Capture(() => Fn.Interpolate(template, refs)), template);
            }
        }

        [TestMethod]
        public void SameTemplateWithDifferentRefs()
        {
            const string template = "https://{Bucket}.s3.{Region}.{partitionResult#dnsSuffix}";
            var refs = CreateRefs();
            Assert.AreEqual("https://my-application-bucket.s3.us-west-2.amazonaws.com", Fn.Interpolate(template, refs));

            refs["Bucket"] = "other";
            refs["Region"] = "cn-north-1";
            refs["partitionResult"] = Fn.Partition("cn-north-1");
            Assert.AreEqual("https://other.s3.cn-north-1.amazonaws.com.cn", Fn.Interpolate(template, refs));

            refs.Remove("Bucket");
            Assert.AreEqual(Capture(() => OriginalInterpolate(template, refs)), Capture(() => Fn.Interpolate(template, refs)));

            refs["Bucket"] = "b";
            refs["partitionResult"] = "not a bag";
            Assert.AreEqual(Capture(() => OriginalInterpolate(template, refs)), Capture(() => Fn.Interpolate(template, refs)));
        }

        [TestMethod]
        public void NullArguments()
        {
            var refs = CreateRefs();
            Assert.AreEqual(Capture(() => OriginalInterpolate(null, refs)), Capture(() => Fn.Interpolate(null, refs)));
            Assert.AreEqual(Capture(() => OriginalInterpolate("{Region}", null)), Capture(() => Fn.Interpolate("{Region}", null)));
            Assert.AreEqual(Capture(() => OriginalInterpolate("no refs used", null)), Capture(() => Fn.Interpolate("no refs used", null)));
            Assert.AreEqual(Capture(() => OriginalInterpolate("{{}}", null)), Capture(() => Fn.Interpolate("{{}}", null)));
        }

        [TestMethod]
        public void ConcurrentCallsReturnCorrectResults()
        {
            var refs = CreateRefs();
            var template = new string("https://{Bucket}.s3.{Region}.{partitionResult#dnsSuffix}".ToCharArray());
            Parallel.For(0, 2000, i =>
            {
                Assert.AreEqual("https://my-application-bucket.s3.us-west-2.amazonaws.com", Fn.Interpolate(template, refs));
            });
        }

        [TestMethod]
        public void InterpolateJsonDoesNotAddTemplatesToTheCache()
        {
            var refs = CreateRefs();
            var before = CacheCount();
            for (int i = 0; i < 50; i++)
            {
                Assert.AreEqual(
                    "{\"authSchemes\":[{\"name\":\"sigv4\",\"signingRegion\":\"us-west-2\"}]}",
                    Fn.InterpolateJson("{\"authSchemes\":[{\"name\":\"sigv4\",\"signingRegion\":\"{Region}\"}]}", refs));
            }
            if (before >= 0)
            {
                Assert.AreEqual(before, CacheCount());
            }
        }

        [TestMethod]
        public void ManyDistinctTemplatesStayCorrect()
        {
            var refs = CreateRefs();
            for (int i = 0; i < 3000; i++)
            {
                var template = "https://{Bucket}.s3-" + i + ".{Region}.{partitionResult#dnsSuffix}";
                Assert.AreEqual("https://my-application-bucket.s3-" + i + ".us-west-2.amazonaws.com", Fn.Interpolate(template, refs));
            }
            var count = CacheCount();
            if (count >= 0)
            {
                Assert.IsTrue(count <= 1024, $"cache holds {count} templates");
            }
            Assert.AreEqual("https://my-application-bucket.s3.us-west-2.amazonaws.com",
                Fn.Interpolate(new string("https://{Bucket}.s3.{Region}.{partitionResult#dnsSuffix}".ToCharArray()), refs));
        }

        // -1 when the implementation has no template cache.
        private static int CacheCount()
        {
            var type = typeof(Fn).GetNestedType("InterpolationTemplate", BindingFlags.NonPublic);
            var field = type?.GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static);
            if (field == null)
            {
                return -1;
            }
            return (int)field.FieldType.GetProperty("Count").GetValue(field.GetValue(null));
        }

        private static string Capture(Func<string> action)
        {
            try
            {
                var value = action();
                return value == null ? "<null>" : "value:" + value;
            }
            catch (Exception e)
            {
                return "throws:" + e.GetType().FullName + ":" + e.Message;
            }
        }

        // Copy of Fn.Interpolate before templates were cached.
        private static string OriginalInterpolate(string template, Dictionary<string, object> refs)
        {
            const char OpenBracket = '{';
            const char CloseBracket = '}';
            const char Hash = '#';
            var result = new StringBuilder(template.Length);
            var remainder = template.AsSpan();
            while (remainder.Length > 0)
            {
                var specialPos = remainder.IndexOfAny(OpenBracket, CloseBracket);
                if (specialPos < 0)
                {
                    result.Append(remainder);
                    break;
                }

                if (specialPos > 0)
                {
                    result.Append(remainder.Slice(0, specialPos));
                    remainder = remainder.Slice(specialPos);
                }

                var currentChar = remainder[0];
                var nextChar = remainder.Length > 1 ? remainder[1] : default(char);

                if (currentChar == nextChar)
                {
                    result.Append(currentChar);
                    remainder = remainder.Slice(2);
                    continue;
                }

                if (currentChar == CloseBracket)
                    throw new ArgumentException("template has non-matching closing bracket, use }} to output }");

                var closingBracket = remainder.IndexOf(CloseBracket);
                if (closingBracket < 0)
                    throw new ArgumentException("template is missing closing }");

                var segment = remainder.Slice(1, closingBracket - 1);
                var hashPos = segment.IndexOf(Hash);
                if (hashPos >= 0)
                {
                    var refName = segment.Slice(0, hashPos).ToString();
                    segment = segment.Slice(hashPos + 1);

                    var secondaryHashPos = segment.IndexOf(Hash);
                    if (secondaryHashPos >= 0)
                        segment = segment.Slice(0, secondaryHashPos);

                    result.Append(Fn.GetAttr(refs[refName], segment.ToString()).ToString());
                }
                else
                {
                    result.Append(refs[segment.ToString()].ToString());
                }
                remainder = remainder.Slice(closingBracket + 1);
            }

            return result.ToString();
        }
    }
}
