using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Amazon.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins AWSSDKUtils.CanonicalizeResourcePathV2 to the segment-list algorithm it used before the single-pass
    /// builder: split into segments, optionally pre-encode each one, then join with JoinResourcePathSegmentsV2.
    /// </summary>
    [TestClass]
    public class CanonicalizeResourcePathEquivalenceTests
    {
        private static readonly Uri[] Endpoints =
        {
            new Uri("https://my-bucket.s3.us-west-2.amazonaws.com"),
            new Uri("https://lambda.us-west-2.amazonaws.com/"),
            new Uri("https://customhost/custompath"),
            new Uri("https://customhost/$custom%20path/"),
        };

        // The pre-change algorithm, built from public members that this change does not touch.
        private static string Reference(Uri endpoint, string resourcePath, bool encode, IDictionary<string, string> pathResources)
        {
            if (endpoint != null)
            {
                var path = endpoint.AbsolutePath;
                if (string.IsNullOrEmpty(path) || string.Equals(path, "/", StringComparison.Ordinal))
                    path = string.Empty;
                if (!string.IsNullOrEmpty(resourcePath) && resourcePath.StartsWith("/", StringComparison.Ordinal))
                    resourcePath = resourcePath.Substring(1);
                if (!string.IsNullOrEmpty(resourcePath))
                    path = path + "/" + resourcePath;
                resourcePath = path;
            }

            if (string.IsNullOrEmpty(resourcePath))
                return "/";

            var segments = AWSSDKUtils.SplitResourcePathIntoSegmentsV2(resourcePath, pathResources);
            if (encode)
            {
                if (endpoint == null)
                    throw new ArgumentNullException(nameof(endpoint));
                foreach (var segment in segments)
                    segment.Value = AWSSDKUtils.UrlEncode(segment.Value, segment.SegmentType != SegmentType.Label);
            }

            return AWSSDKUtils.JoinResourcePathSegmentsV2(segments);
        }

        private static void AssertSame(Uri endpoint, string resourcePath, bool encode, IDictionary<string, string> pathResources)
        {
            var expected = Reference(endpoint, resourcePath, encode, pathResources);
            var actual = AWSSDKUtils.CanonicalizeResourcePathV2(endpoint, resourcePath, encode, pathResources);
            Assert.AreEqual(expected, actual, $"path='{resourcePath}' encode={encode} endpoint={endpoint} resources={Describe(pathResources)}");
        }

        private static string Describe(IDictionary<string, string> pathResources)
            => pathResources == null ? "null" : string.Join(",", pathResources.Select(kv => kv.Key + "=" + (kv.Value ?? "<null>")));

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Util")]
        public void KnownShapesMatchReference()
        {
            var cases = new (string Path, IDictionary<string, string> Resources)[]
            {
                ("/", null),
                ("/a/b/c", null),
                ("//a//b//", null),
                ("/reports/2026/Q4 summary (final).pdf", null),
                ("/already%2Fencoded/x%20y", null),
                ("/2015-03-31/functions/{FunctionName}/invocations", Ordinal(("{FunctionName}", "my-function:prod"))),
                ("/2015-03-31/functions/{FunctionName}/invocations", Ordinal(("{FunctionName}", "arn:aws:lambda:us-west-2:123456789012:function:f/x"))),
                ("/{Key+}", Ordinal(("{Key+}", "photos/2026/11/20/IMG 0001 (copy).jpg"))),
                ("/{Key+}", Ordinal(("{Key+}", "/leading/and/trailing/"))),
                ("/{Key+}", Ordinal(("{Key+}", ""))),
                ("/{Key+}", Ordinal(("{Key+}", "a//b"))),
                ("/{Bucket}/{Key+}", Ordinal(("{Bucket}", "b"), ("{Key+}", "100%/é/😀/~*'()!$&+,;=:@"))),
                ("/{a}/{b}", Ordinal(("{a}", "vx_folder"), ("{b}", @"1.0\datafiles\file.json"))),
                ("/{a}", Ordinal(("{a}", "@#$:!.json"))),
                ("/{a}", Ordinal(("{a}", null))),
                ("/{a}/{a}/{missing}", Ordinal(("{a}", "x y"))),
                ("/{a+}", Ordinal(("{a}", "not/greedy"))),
                ("/lone\uD800surrogate/{a}", Ordinal(("{a}", "\uDC00x"))),
                ("/{A}/{a}", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "{a}", "ci/value" } }),
                ("/{a}/{Key+}", new SortedDictionary<string, string>(StringComparer.Ordinal) { { "{a}", "s v" }, { "{Key+}", "k/v w" } }),
                ("/{a}/b", new Dictionary<string, string>()),
            };

            foreach (var endpoint in Endpoints.Concat(new Uri[] { null }))
            foreach (var (path, resources) in cases)
            foreach (var encode in new[] { false, true })
            {
                if (endpoint == null && encode)
                    continue; // covered by NullEndpointWithEncodeThrows
                AssertSame(endpoint, path, encode, resources);
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Util")]
        public void RandomPathsMatchReference()
        {
            const string alphabet = "aZ09-_.~/%+ {}:@$&=*'()!,;?#[]\\\"é€😀\uD800\uDC00\t";
            var random = new Random(20261120);
            for (int i = 0; i < 4000; i++)
            {
                var path = RandomString(random, alphabet, random.Next(0, 40));
                IDictionary<string, string> resources = null;
                switch (random.Next(4))
                {
                    case 1:
                        resources = Ordinal(("{a}", RandomString(random, alphabet, random.Next(0, 12))),
                                            ("{k+}", RandomString(random, alphabet, random.Next(0, 20))));
                        path = "/" + path.Replace("{", "/{a}/").Replace("}", "/{k+}");
                        break;
                    case 2:
                        resources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            { "{A}", RandomString(random, alphabet, random.Next(0, 12)) },
                        };
                        path = "/" + path.Replace("{", "/{a}/");
                        break;
                    case 3:
                        resources = new Dictionary<string, string>();
                        break;
                }

                var endpoint = Endpoints[random.Next(Endpoints.Length)];
                AssertSame(endpoint, path, random.Next(2) == 0, resources);
                AssertSame(null, path, false, resources);
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Util")]
        public void LongSegmentsMatchReference()
        {
            // Longer than the stack scratch buffer, so the pooled-buffer path runs.
            var longValue = string.Concat(Enumerable.Repeat("seg ment/é%", 60));
            AssertSame(Endpoints[0], "/" + longValue, true, null);
            AssertSame(Endpoints[0], "/" + longValue.Replace("/", ""), false, null);
            AssertSame(Endpoints[0], "/{Key+}", false, Ordinal(("{Key+}", longValue)));
            AssertSame(Endpoints[1], "/x/{a}", true, Ordinal(("{a}", longValue)));
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Util")]
        public void NullEndpointWithEncodeThrows()
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => AWSSDKUtils.CanonicalizeResourcePathV2(null, "/a", true, null));
            Assert.ThrowsExactly<ArgumentNullException>(() => AWSSDKUtils.CanonicalizeResourcePathV2(null, "/{a}", true, Ordinal(("{a}", "x"))));
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Util")]
        public void NullGreedyValueThrowsAsBefore()
        {
            var resources = Ordinal(("{Key+}", null));
            Assert.ThrowsExactly<NullReferenceException>(() => AWSSDKUtils.CanonicalizeResourcePathV2(Endpoints[0], "/{Key+}", false, resources));
            Assert.ThrowsExactly<NullReferenceException>(() => AWSSDKUtils.CanonicalizeResourcePathV2(Endpoints[0], "/{Key+}", true, resources));
        }

        private static Dictionary<string, string> Ordinal(params (string Key, string Value)[] entries)
        {
            var dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in entries)
                dictionary[key] = value;
            return dictionary;
        }

        private static string RandomString(Random random, string alphabet, int length)
        {
            var builder = new StringBuilder(length);
            for (int i = 0; i < length; i++)
                builder.Append(alphabet[random.Next(alphabet.Length)]);
            return builder.ToString();
        }
    }
}
