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
using Amazon.Runtime;
using Amazon.Runtime.Internal.Auth;
using Amazon.Runtime.Internal.Util;
using Amazon.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins the signed trailer that ChunkedUploadWrapperStream appends for SigV4 to a reference built with the
    /// expressions the stream used before (string concatenation, SignBlob, ToHex, OrderBy and a StringBuilder),
    /// for one and several trailing headers, with and without a trailing checksum, and for small reads that copy
    /// the trailer in pieces.
    /// </summary>
    [TestClass]
    public class ChunkedUploadTrailerEquivalenceTests
    {
        private const int ChunkSize = 81920;
        private static readonly DateTime SignedAt = new DateTime(2026, 11, 21, 5, 6, 7, DateTimeKind.Utc);
        private const string Scope = "20261121/us-west-2/s3/aws4_request";
        private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");

        private static byte[] Bytes(int length, int seed)
        {
            var data = new byte[length];
            new Random(seed).NextBytes(data);
            return data;
        }

        private static AWS4SigningResult SigningResult(int signatureLength = 32, string scope = Scope, byte[] signingKey = null)
        {
            return new AWS4SigningResult("AKIDEXAMPLE", "secret", SignedAt, "host;x-amz-date", scope,
                signingKey ?? Bytes(32, 1), Bytes(signatureLength, 2));
        }

        // The whole stream as the string-based code produced it. trailingHeaders already holds the final checksum value.
        private static string Reference(byte[] data, AWS4SigningResult result, IDictionary<string, string> trailingHeaders)
        {
            var key = result.GetSigningKey();
            var previous = result.Signature;
            var output = new StringBuilder();
            int offset = 0;
            while (true)
            {
                int len = Math.Min(ChunkSize, data.Length - offset);
                var chunk = new byte[len];
                Buffer.BlockCopy(data, offset, chunk, 0, len);
                offset += len;
                var stringToSign = ChunkedUploadWrapperStream.BuildChunkedStringToSign("AWS4-HMAC-SHA256-PAYLOAD", result.ISO8601DateTime, result.Scope, previous, len, chunk);
                previous = AWSSDKUtils.ToHex(AWS4Signer.SignBlob(key, stringToSign), true);
                output.Append(len.ToString("X")).Append(";chunk-signature=").Append(previous).Append("\r\n");
                output.Append(Latin1.GetString(chunk));
                if (len != 0)
                    output.Append("\r\n");
                if (len == 0)
                    break;
            }

            var canonical = string.Concat(trailingHeaders.Select(h => new KeyValuePair<string, string>(h.Key.ToLowerInvariant(), h.Value))
                .OrderBy(h => h.Key, StringComparer.Ordinal)
                .Select(h => h.Key + ":" + AWSSDKUtils.CompressSpaces(h.Value, trim: true) + "\n"));
            var trailerStringToSign = "AWS4-HMAC-SHA256-TRAILER\n" + result.ISO8601DateTime + "\n" + result.Scope + "\n" + previous + "\n" +
                                      AWSSDKUtils.ToHex(AWS4Signer.ComputeHash(canonical), true);
            var signature = AWSSDKUtils.ToHex(AWS4Signer.SignBlob(key, trailerStringToSign), true);
            foreach (var kvp in trailingHeaders.OrderBy(kvp => kvp.Key))
                output.Append($"{kvp.Key}:{kvp.Value}\r\n");
            output.Append($"x-amz-trailer-signature:{signature}\r\n");
            output.Append("\r\n");
            return output.ToString();
        }

        private static string Crc32Base64(byte[] data)
        {
            return Convert.ToBase64String(CryptoUtilFactory.GetChecksumInstance(CoreChecksumAlgorithm.CRC32).ComputeHash(data));
        }

        private static string ReadAll(Stream stream, int readSize)
        {
            var buffer = new byte[readSize];
            var output = new MemoryStream();
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                output.Write(buffer, 0, read);
            return Latin1.GetString(output.ToArray());
        }

        private static async Task<string> ReadAllAsync(Stream stream, int readSize)
        {
            var buffer = new byte[readSize];
            var output = new MemoryStream();
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                output.Write(buffer, 0, read);
            return Latin1.GetString(output.ToArray());
        }

        private static ChunkedUploadWrapperStream Create(byte[] data, AWS4SigningResult result, CoreChecksumAlgorithm checksum, IDictionary<string, string> trailingHeaders)
        {
            return new ChunkedUploadWrapperStream(new MemoryStream(data, writable: false), 8192, result, checksum, trailingHeaders,
                new CachedChecksum { OnComplete = _ => { } });
        }

        private static readonly int[] Sizes = { 0, 1, 4096, ChunkSize + 1 };
        private static readonly int[] ReadSizes = { 1, 7, 100, ChunkSize };

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        [DataRow(32)]
        [DataRow(64)]
        public void SingleTrailingChecksumMatchesReference(int seedSignatureBytes)
        {
            var result = SigningResult(seedSignatureBytes);
            foreach (var size in Sizes)
            {
                var data = Bytes(size, size + 3);
                var expected = Reference(data, result, new Dictionary<string, string> { { "x-amz-checksum-crc32", Crc32Base64(data) } });
                foreach (var readSize in ReadSizes)
                {
                    if (readSize == 1 && size > ChunkSize)
                        continue;
                    var trailing = new Dictionary<string, string> { { "x-amz-checksum-crc32", "" } };
                    Assert.AreEqual(expected, ReadAll(Create(data, result, CoreChecksumAlgorithm.CRC32, trailing), readSize), $"size {size}, read {readSize}");
                }
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void SeveralTrailingHeadersMatchReference()
        {
            var data = Bytes(5000, 4);
            var result = SigningResult();

            // Checksum plus other headers, in an order that is not sorted, with mixed case and spaces in a value.
            Func<Dictionary<string, string>> withChecksum = () => new Dictionary<string, string>
            {
                { "x-amz-meta-b", "  two   spaces  " },
                { "x-amz-checksum-sha256", "" },
                { "X-Amz-Meta-A", "value" },
            };
            var expected = withChecksum();
            expected["x-amz-checksum-sha256"] = Convert.ToBase64String(CryptoUtilFactory.GetChecksumInstance(CoreChecksumAlgorithm.SHA256).ComputeHash(data));
            foreach (var readSize in ReadSizes)
            {
                Assert.AreEqual(Reference(data, result, expected), ReadAll(Create(data, result, CoreChecksumAlgorithm.SHA256, withChecksum()), readSize), $"read {readSize}");
            }

            // Trailing headers without a checksum.
            Func<Dictionary<string, string>> noChecksum = () => new Dictionary<string, string> { { "x-amz-meta-z", "1" }, { "x-amz-meta-y", "" } };
            foreach (var readSize in ReadSizes)
            {
                Assert.AreEqual(Reference(data, result, noChecksum()), ReadAll(Create(data, result, CoreChecksumAlgorithm.NONE, noChecksum()), readSize), $"no checksum, read {readSize}");
            }

            // A single non-checksum header, and a single header with an empty value.
            foreach (var single in new[] { "x-amz-meta-only", "" })
            {
                Func<Dictionary<string, string>> one = () => new Dictionary<string, string> { { "x-amz-meta-only", single } };
                Assert.AreEqual(Reference(data, result, one()), ReadAll(Create(data, result, CoreChecksumAlgorithm.NONE, one()), 7), $"single '{single}'");
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public async Task AsyncReadsMatchReference()
        {
            var result = SigningResult();
            foreach (var size in Sizes)
            {
                var data = Bytes(size, size + 9);
                var expected = Reference(data, result, new Dictionary<string, string> { { "x-amz-checksum-crc32", Crc32Base64(data) } });
                var trailing = new Dictionary<string, string> { { "x-amz-checksum-crc32", "" } };
                Assert.AreEqual(expected, await ReadAllAsync(Create(data, result, CoreChecksumAlgorithm.CRC32, trailing), 33), $"size {size}");
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void LongAndNonAsciiScopeAndAnyKeyLengthMatchReference()
        {
            var data = Bytes(300, 5);
            var scopes = new[] { "20261121/eu-wést-1/s3/aws4_request", "20261121/\uD83D\uDE00/s3/aws4_request", "", null, "20261121/" + new string('r', 600) + "/s3/aws4_request" };
            foreach (var scope in scopes)
            {
                foreach (var keyLength in new[] { 1, 32, 65, 200 })
                {
                    var result = SigningResult(scope: scope, signingKey: Bytes(keyLength, keyLength));
                    var expected = Reference(data, result, new Dictionary<string, string> { { "x-amz-checksum-crc32", Crc32Base64(data) } });
                    var trailing = new Dictionary<string, string> { { "x-amz-checksum-crc32", "" } };
                    Assert.AreEqual(expected, ReadAll(Create(data, result, CoreChecksumAlgorithm.CRC32, trailing), 64), $"scope {scope?.Length}, key {keyLength}");
                }
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void EmptySigningKeyStillThrowsArgumentNullException()
        {
            var result = SigningResult(signingKey: new byte[0]);
            var trailing = new Dictionary<string, string> { { "x-amz-checksum-crc32", "" } };
            Assert.ThrowsExactly<ArgumentNullException>(() => ReadAll(Create(Bytes(10, 1), result, CoreChecksumAlgorithm.CRC32, trailing), 4096));
        }
    }
}
