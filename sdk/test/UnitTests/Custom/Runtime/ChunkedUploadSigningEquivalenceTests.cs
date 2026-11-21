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
    /// Pins the bytes ChunkedUploadWrapperStream produces for SigV4 to a reference built from the public string-based
    /// helpers (BuildChunkedStringToSign, SignBlob, ToHex) that the stream used for every chunk before it switched to a
    /// reusable UTF-8 string to sign. Covers both the fast path (a 64-character seed signature) and the fallback.
    /// </summary>
    [TestClass]
    public class ChunkedUploadSigningEquivalenceTests
    {
        private const int ChunkSize = 81920;
        private static readonly DateTime SignedAt = new DateTime(2026, 11, 21, 5, 6, 7, DateTimeKind.Utc);
        private const string Scope = "20261121/us-west-2/s3/aws4_request";

        private static byte[] Bytes(int length, int seed)
        {
            var data = new byte[length];
            new Random(seed).NextBytes(data);
            return data;
        }

        private static AWS4SigningResult SigningResult(int signatureLength, string scope = Scope, byte[] signingKey = null)
        {
            return new AWS4SigningResult("AKIDEXAMPLE", "secret", SignedAt, "host;x-amz-date", scope,
                signingKey ?? Bytes(32, 1), Bytes(signatureLength, 2));
        }

        private static string Reference(byte[] data, AWS4SigningResult result, string checksumKey, string checksumValue)
        {
            var key = result.GetSigningKey();
            var previous = result.Signature;
            var output = new StringBuilder();
            var hasTrailers = checksumKey != null;
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
                output.Append(Encoding.GetEncoding("ISO-8859-1").GetString(chunk));
                if (!(len == 0 && hasTrailers))
                    output.Append("\r\n");
                if (len == 0)
                    break;
            }

            if (hasTrailers)
            {
                var canonical = checksumKey + ":" + checksumValue + "\n";
                var stringToSign = "AWS4-HMAC-SHA256-TRAILER\n" + result.ISO8601DateTime + "\n" + result.Scope + "\n" + previous + "\n" +
                                   AWSSDKUtils.ToHex(AWS4Signer.ComputeHash(canonical), true);
                var signature = AWSSDKUtils.ToHex(AWS4Signer.SignBlob(key, stringToSign), true);
                output.Append(checksumKey).Append(':').Append(checksumValue).Append("\r\n");
                output.Append("x-amz-trailer-signature:").Append(signature).Append("\r\n\r\n");
            }
            return output.ToString();
        }

        private static string Crc32Base64(byte[] data)
        {
            var algorithm = CryptoUtilFactory.GetChecksumInstance(CoreChecksumAlgorithm.CRC32);
            return Convert.ToBase64String(algorithm.ComputeHash(data));
        }

        private static ChunkedUploadWrapperStream Create(byte[] data, AWS4SigningResult result, bool trailingChecksum, int wrappedBufferSize = 8192)
        {
            var stream = new MemoryStream(data, writable: false);
            if (!trailingChecksum)
            {
                return new ChunkedUploadWrapperStream(stream, wrappedBufferSize, result);
            }
            var trailing = new Dictionary<string, string> { { "x-amz-checksum-crc32", "" } };
            return new ChunkedUploadWrapperStream(stream, wrappedBufferSize, result, CoreChecksumAlgorithm.CRC32, trailing,
                new CachedChecksum { OnComplete = _ => { } });
        }

        private static string ReadAll(Stream stream, int readSize)
        {
            var buffer = new byte[readSize];
            var output = new MemoryStream();
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                output.Write(buffer, 0, read);
            return Encoding.GetEncoding("ISO-8859-1").GetString(output.ToArray());
        }

        private static async Task<string> ReadAllAsync(Stream stream, int readSize)
        {
            var buffer = new byte[readSize];
            var output = new MemoryStream();
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                output.Write(buffer, 0, read);
            return Encoding.GetEncoding("ISO-8859-1").GetString(output.ToArray());
        }

        private static readonly int[] Sizes = { 0, 1, 11, 4096, ChunkSize - 1, ChunkSize, ChunkSize + 1, 2 * ChunkSize + 17 };

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        [DataRow(32, DisplayName = "64-character seed signature (fast path on .NET 8+)")]
        [DataRow(64, DisplayName = "128-character seed signature (string path)")]
        [DataRow(20, DisplayName = "40-character seed signature (string path)")]
        public void ChunksMatchReference(int seedSignatureBytes)
        {
            var result = SigningResult(seedSignatureBytes);
            foreach (var size in Sizes)
            {
                var data = Bytes(size, size + 3);
                var expected = Reference(data, result, null, null);
                foreach (var readSize in new[] { 1, 7, 8192, ChunkSize, 3 * ChunkSize })
                {
                    if (readSize == 1 && size > ChunkSize)
                        continue; // slow and adds nothing over 7
                    var stream = Create(data, result, trailingChecksum: false);
                    Assert.AreEqual(expected, ReadAll(stream, readSize), $"size {size}, read {readSize}");
                    Assert.AreEqual(expected.Length, stream.Length, $"length, size {size}");
                }
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        [DataRow(32)]
        [DataRow(64)]
        public void TrailingChecksumMatchesReference(int seedSignatureBytes)
        {
            var result = SigningResult(seedSignatureBytes);
            foreach (var size in Sizes)
            {
                var data = Bytes(size, size + 5);
                var expected = Reference(data, result, "x-amz-checksum-crc32", Crc32Base64(data));
                foreach (var readSize in new[] { 7, 100, ChunkSize })
                {
                    Assert.AreEqual(expected, ReadAll(Create(data, result, trailingChecksum: true), readSize), $"size {size}, read {readSize}");
                }
                Assert.AreEqual(expected.Length, Create(data, result, trailingChecksum: true).Length, $"length, size {size}");
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public async Task AsyncReadsMatchReference()
        {
            var result = SigningResult(32);
            foreach (var size in Sizes)
            {
                var data = Bytes(size, size + 7);
                Assert.AreEqual(Reference(data, result, null, null), await ReadAllAsync(Create(data, result, false, ChunkSize), 8192), $"size {size}");
                Assert.AreEqual(Reference(data, result, "x-amz-checksum-crc32", Crc32Base64(data)),
                    await ReadAllAsync(Create(data, result, true, 1000), 333), $"trailer, size {size}");
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void NonAsciiAndNullScopeMatchReference()
        {
            var data = Bytes(5000, 9);
            foreach (var scope in new[] { "20261121/eu-wést-1/s3/aws4_request", "20261121/\uD83D\uDE00/s3/aws4_request", "", null })
            {
                var result = SigningResult(32, scope);
                Assert.AreEqual(Reference(data, result, null, null), ReadAll(Create(data, result, false), 4096), scope ?? "null");
                Assert.AreEqual(Reference(data, result, "x-amz-checksum-crc32", Crc32Base64(data)), ReadAll(Create(data, result, true), 4096), scope ?? "null");
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void SigningKeyOfAnyLengthMatchesReference()
        {
            var data = Bytes(300, 11);
            foreach (var keyLength in new[] { 1, 16, 32, 64, 65, 200 })
            {
                var result = SigningResult(32, signingKey: Bytes(keyLength, keyLength));
                Assert.AreEqual(Reference(data, result, null, null), ReadAll(Create(data, result, false), 4096), $"key {keyLength}");
            }
        }

        [TestMethod]
        [TestCategory("UnitTest")]
        [TestCategory("Runtime")]
        public void EmptySigningKeyStillThrowsArgumentNullException()
        {
            var result = SigningResult(32, signingKey: new byte[0]);
            var stream = Create(Bytes(10, 1), result, false);
            Assert.ThrowsExactly<ArgumentNullException>(() => ReadAll(stream, 4096));
        }
    }
}
