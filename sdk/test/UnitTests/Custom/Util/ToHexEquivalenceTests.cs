using System;
using System.Text;
using Amazon.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins AWSSDKUtils.ToHex to a reference hex encoder for both cases, and its null handling.
    /// </summary>
    [TestClass]
    public class ToHexEquivalenceTests
    {
        private static string Reference(byte[] data, bool lowercase)
        {
            var builder = new StringBuilder(data.Length * 2);
            foreach (var b in data)
                builder.Append(b.ToString(lowercase ? "x2" : "X2"));
            return builder.ToString();
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Util")]
        public void MatchesReference()
        {
            var random = new Random(20261121);
            foreach (var length in new[] { 0, 1, 2, 15, 16, 17, 20, 31, 32, 33, 48, 63, 64, 65, 100, 1000 })
            {
                var data = new byte[length];
                random.NextBytes(data);
                Assert.AreEqual(Reference(data, true), AWSSDKUtils.ToHex(data, true), "lower, length " + length);
                Assert.AreEqual(Reference(data, false), AWSSDKUtils.ToHex(data, false), "upper, length " + length);
            }

            var all = new byte[256];
            for (int i = 0; i < all.Length; i++)
                all[i] = (byte)i;
            Assert.AreEqual(Reference(all, true), AWSSDKUtils.ToHex(all, true));
            Assert.AreEqual(Reference(all, false), AWSSDKUtils.ToHex(all, false));
            Assert.AreEqual(string.Empty, AWSSDKUtils.ToHex(new byte[0], true));
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Util")]
        public void NullThrowsAsBefore()
        {
            // Lowercase has always dereferenced the array; uppercase on .NET 8+ goes through Convert.ToHexString.
            Assert.ThrowsException<NullReferenceException>(() => AWSSDKUtils.ToHex(null, true));
#if NET8_0_OR_GREATER
            Assert.ThrowsException<ArgumentNullException>(() => AWSSDKUtils.ToHex(null, false));
#else
            Assert.ThrowsException<NullReferenceException>(() => AWSSDKUtils.ToHex(null, false));
#endif
        }
    }
}
