using System;
using System.Collections.Generic;
using System.Linq;
using Amazon.Runtime.Internal.Auth;
using Amazon.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins AWS4Signer.SortAndPruneHeaders to its pre-cache behavior: ignored headers dropped, every other
    /// name lowercased with ToLowerInvariant, ordinal order, and the same exceptions.
    /// </summary>
    [TestClass]
    public class AWS4SignerHeaderNameTests
    {
        private static readonly HashSet<string> Ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            HeaderKeys.XAmznTraceIdHeader, HeaderKeys.TransferEncodingHeader, HeaderKeys.AmzSdkInvocationId,
            HeaderKeys.AmzSdkRequest, HeaderKeys.UserAgentHeader, HeaderKeys.XAmzUserAgentHeader,
        };

        private static List<KeyValuePair<string, string>> Reference(IEnumerable<KeyValuePair<string, string>> headers)
        {
            var sorted = new SortedList<string, string>(StringComparer.Ordinal);
            foreach (var header in headers)
            {
                if (Ignored.Contains(header.Key)) continue;
                sorted.Add(header.Key.ToLowerInvariant(), header.Value);
            }
            return sorted.ToList();
        }

        private static void AssertSame(IEnumerable<KeyValuePair<string, string>> headers)
        {
            var expected = Reference(headers);
            var actual = AWS4Signer.SortAndPruneHeaders(headers).ToList();
            CollectionAssert.AreEqual(expected, actual);
        }

        private static Dictionary<string, string> Headers(params string[] names)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < names.Length; i++)
                headers[names[i]] = "value " + i;
            return headers;
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void TypicalHeadersMatchReference()
        {
            var headers = Headers("Content-Type", "Content-Length", "X-Amz-Target", "X-Amz-Date", "X-Amz-Content-SHA256",
                "x-amz-security-token", "host", "User-Agent", "amz-sdk-invocation-id", "amz-sdk-request", "X-Amzn-Trace-Id",
                "Transfer-Encoding", "x-amz-user-agent", "X_Underscore", "x-amz-meta-ÄÖÜ", "İstanbul", "ǅ-title", "x-amz-meta-ΣΑΣ");

            // Twice, so the second call reads cached names.
            AssertSame(headers);
            AssertSame(headers);

            // The same names through the non-Dictionary path.
            AssertSame(headers.ToList());
            AssertSame(new SortedDictionary<string, string>(headers, StringComparer.Ordinal));
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void ManyDistinctNamesMatchReference()
        {
            // More names than the cache holds, so names past the bound are lowercased per call.
            var names = Enumerable.Range(0, 600).Select(i => "X-Amz-Meta-Name" + i + (i % 3 == 0 ? "ÆØÅ" : "")).ToArray();
            for (int i = 0; i < 3; i++)
            {
                AssertSame(Headers(names));
                AssertSame(Headers(names).ToList());
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void NamesThatCollideWhenLowercasedThrowAsBefore()
        {
            var headers = new Dictionary<string, string>(StringComparer.Ordinal) { { "X-Dup", "1" }, { "x-dup", "2" } };
            Assert.ThrowsExactly<ArgumentException>(() => Reference(headers));
            Assert.ThrowsExactly<ArgumentException>(() => AWS4Signer.SortAndPruneHeaders(headers));
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void NullNameThrowsAsBefore()
        {
            var headers = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>(null, "v") };
            var expected = Capture(() => Reference(headers));
            Assert.IsNotNull(expected);
            Assert.AreEqual(expected.GetType(), Capture(() => AWS4Signer.SortAndPruneHeaders(headers))?.GetType());
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void EmptyHeaders()
        {
            AssertSame(new Dictionary<string, string>());
            AssertSame(new List<KeyValuePair<string, string>>());
        }

        private static Exception Capture(Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        }
    }
}
