using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Amazon.Runtime;
using Amazon.Runtime.Internal.Auth;
using Amazon.Runtime.Internal.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins the trailing checksum header name and the x-amz-trailer header value to the expressions used
    /// before they were replaced with constants and a single-header fast path.
    /// </summary>
    [TestClass]
    public class TrailingHeaderNameTests
    {
        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void ChecksumHeaderKeyMatchesReferenceInEveryCulture()
        {
            var original = Thread.CurrentThread.CurrentCulture;
            try
            {
                foreach (var culture in new[] { "en-US", "tr-TR", "az-Latn-AZ", "lt-LT", "" })
                {
                    Thread.CurrentThread.CurrentCulture = new CultureInfo(culture);
                    foreach (CoreChecksumAlgorithm algorithm in Enum.GetValues(typeof(CoreChecksumAlgorithm)))
                    {
                        var expected = "x-amz-checksum-" + algorithm.ToString().ToLower();
                        Assert.AreEqual(expected, ChecksumUtils.GetChecksumHeaderKey(algorithm), culture + " " + algorithm);
                    }

                    // A value outside the enum keeps the old formatting.
                    Assert.AreEqual("x-amz-checksum-" + ((CoreChecksumAlgorithm)99).ToString().ToLower(), ChecksumUtils.GetChecksumHeaderKey((CoreChecksumAlgorithm)99));
                }
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
            }
        }

        private static string Reference(IDictionary<string, string> trailingHeaders)
            => string.Join(",", trailingHeaders.Keys.OrderBy(key => key).ToArray());

        private static void AssertSame(IDictionary<string, string> trailingHeaders)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "Host", "example.com" } };
            AWS4Signer.SetXAmzTrailerHeader(headers, trailingHeaders);
            Assert.AreEqual(Reference(trailingHeaders), headers["X-Amz-Trailer"]);
            Assert.AreEqual(2, headers.Count);
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void TrailerHeaderMatchesReference()
        {
            AssertSame(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { { "x-amz-checksum-crc32", "" } });
            AssertSame(new Dictionary<string, string> { { "X-Amz-Checksum-SHA256", "abc" } });
            AssertSame(new SortedDictionary<string, string> { { "x-amz-checksum-crc64nvme", "" } });
            AssertSame(new Dictionary<string, string> { { "x-amz-checksum-sha1", "" }, { "x-amz-checksum-crc32", "" }, { "b", "1" } });
            AssertSame(new SortedList<string, string> { { "z", "" }, { "a", "" } });
            AssertSame(new Dictionary<string, string> { { "", "" } });

            // None or null leaves the headers alone.
            var headers = new Dictionary<string, string>();
            AWS4Signer.SetXAmzTrailerHeader(headers, new Dictionary<string, string>());
            AWS4Signer.SetXAmzTrailerHeader(headers, null);
            Assert.AreEqual(0, headers.Count);

            // Replaces an existing value (a resign).
            headers["X-Amz-Trailer"] = "old";
            AWS4Signer.SetXAmzTrailerHeader(headers, new Dictionary<string, string> { { "x-amz-checksum-crc32c", "" } });
            Assert.AreEqual("x-amz-checksum-crc32c", headers["X-Amz-Trailer"]);
        }
    }
}
