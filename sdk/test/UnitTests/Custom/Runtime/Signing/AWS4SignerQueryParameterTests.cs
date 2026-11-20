using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;
using Amazon.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins AWS4Signer's canonical query string and parameter collection to the algorithm used before the
    /// encoded values were appended directly and already-sorted lists were no longer copied.
    /// </summary>
    [TestClass]
    public class AWS4SignerQueryParameterTests
    {
        private sealed class Access : AWS4Signer
        {
            public static string Canonicalize(IEnumerable<KeyValuePair<string, string>> parameters, bool encode)
                => CanonicalizeQueryParameters(parameters, encode);
        }

        private static string Reference(IEnumerable<KeyValuePair<string, string>> parameters, bool encode)
        {
            if (parameters == null)
                return string.Empty;
            var sorted = new List<KeyValuePair<string, string>>(parameters);
            if (sorted.Count == 0)
                return string.Empty;
            sorted.Sort((x, y) => StringComparer.Ordinal.Compare(x.Key, y.Key));

            var builder = new StringBuilder();
            for (int i = 0; i < sorted.Count; i++)
            {
                var key = encode ? AWSSDKUtils.UrlEncode(sorted[i].Key, false) : sorted[i].Key;
                var value = encode ? AWSSDKUtils.UrlEncode(sorted[i].Value, false) : sorted[i].Value;
                if (i > 0) builder.Append('&');
                builder.Append(key).Append('=');
                if (!string.IsNullOrEmpty(value))
                    builder.Append(value);
            }
            return builder.ToString();
        }

        private static void AssertSame(List<KeyValuePair<string, string>> parameters)
        {
            var snapshot = parameters.ToList();
            foreach (var encode in new[] { true, false })
            {
                Assert.AreEqual(Reference(parameters, encode), Access.Canonicalize(parameters, encode));
                Assert.AreEqual(Reference(parameters, encode), Access.Canonicalize(parameters.ToArray(), encode));
                Assert.AreEqual(Reference(parameters, encode), Access.Canonicalize(parameters.Select(p => p), encode));
            }
            CollectionAssert.AreEqual(snapshot, parameters, "the input list must not be modified");
        }

        private static KeyValuePair<string, string> P(string key, string value) => new KeyValuePair<string, string>(key, value);

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void KnownShapesMatchReference()
        {
            AssertSame(new List<KeyValuePair<string, string>>());
            AssertSame(new List<KeyValuePair<string, string>> { P("list-type", "2") });
            AssertSame(new List<KeyValuePair<string, string>>
            {
                P("continuation-token", "1ueGcxLPRx1Tr/XYExHnhbYLgveDs2J/wm36Hy4vbOwM="),
                P("list-type", "2"),
                P("max-keys", "1000"),
                P("prefix", "logs/2026/11/20/"),
            });
            // Unsorted, duplicate keys, null and empty values, keys that need encoding, non-ASCII, lone surrogates.
            AssertSame(new List<KeyValuePair<string, string>>
            {
                P("b", "2"), P("a", null), P("a", ""), P("A", "x y"), P("é", "ü/€"), P("k\uD800", "\uDC00v"),
                P("", "empty-key"), P(null, "null-key"), P("X-Amz-Signature", "abc"), P("a", "dup"),
            });
            // Strictly ascending except for one pair.
            AssertSame(new List<KeyValuePair<string, string>> { P("a", "1"), P("c", "3"), P("b", "2"), P("d", "4") });
            // Ascending with an equal pair: must be sorted exactly as before.
            AssertSame(new List<KeyValuePair<string, string>> { P("a", "1"), P("b", "2"), P("b", "1"), P("c", "3") });
            // Long values, longer than the stack buffer.
            AssertSame(new List<KeyValuePair<string, string>> { P("a", new string('é', 500)), P("b", string.Concat(Enumerable.Repeat("x y/", 300))) });
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void RandomParametersMatchReference()
        {
            const string alphabet = "aZ09-_.~ /%+=&;:@é€\uD800\uDC00";
            var random = new Random(20261120);
            for (int i = 0; i < 2000; i++)
            {
                var count = random.Next(0, 24);
                var list = new List<KeyValuePair<string, string>>();
                for (int j = 0; j < count; j++)
                {
                    var key = random.Next(10) == 0 ? null : RandomString(random, alphabet, random.Next(0, 4));
                    var value = random.Next(8) == 0 ? null : RandomString(random, alphabet, random.Next(0, 12));
                    list.Add(P(key, value));
                }
                if (random.Next(2) == 0)
                    list.Sort((x, y) => StringComparer.Ordinal.Compare(x.Key, y.Key));
                AssertSame(list);
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void ParametersToCanonicalizeMatchReference()
        {
            var request = new DefaultRequest(new StubRequest(), "Bench") { UseQueryString = true };
            request.SubResources["versionId"] = "v 1";
            request.SubResources["acl"] = null;
            request.ParameterCollection.Add("prefix", "logs/");
            request.ParameterCollection.Add("tags", new List<string> { "z", "a", "m" });
            request.ParameterCollection.Add("nullValue", (string)null);
            request.ParameterCollection.Add("max-keys", "1000");

            var expected = new List<KeyValuePair<string, string>>();
            foreach (var subResource in request.SubResources)
                expected.Add(P(subResource.Key, subResource.Value));
            expected.AddRange(request.ParameterCollection.GetSortedParametersList().Where(p => p.Value != null));

            CollectionAssert.AreEqual(expected, AWS4Signer.GetParametersToCanonicalize(request));

            request.UseQueryString = false;
            CollectionAssert.AreEqual(expected.Take(2).ToList(), AWS4Signer.GetParametersToCanonicalize(request));
        }

        private static string RandomString(Random random, string alphabet, int length)
        {
            var builder = new StringBuilder(length);
            for (int i = 0; i < length; i++)
                builder.Append(alphabet[random.Next(alphabet.Length)]);
            return builder.ToString();
        }

        private sealed class StubRequest : AmazonWebServiceRequest { }
    }
}
