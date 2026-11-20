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

using System;
using System.Text;
using Amazon.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Checks AWSSDKUtils.CompressSpaces against a copy of its previous scalar implementation, so the
    /// vectorized fast path on .NET 8+ is shown to return exactly what the old code returned, including
    /// for inputs where the old code left whitespace in place.
    /// </summary>
    [TestClass]
    [TestCategory("Core")]
    public class CompressSpacesEquivalenceTests
    {
        private static readonly char[] Alphabet = { 'a', 'b', ' ', '\t', '\n', '\r', '\u00A0', '\u2007', '\u3000', '\u0085', '\u200B' };

        [TestMethod]
        public void CompressSpaces_ExhaustiveShortInputs_MatchReference()
        {
            // Every string of length 0..5 over the alphabet (about 177k strings).
            var buffer = new char[5];
            for (int length = 0; length <= buffer.Length; length++)
            {
                var total = (int)Math.Pow(Alphabet.Length, length);
                for (int n = 0; n < total; n++)
                {
                    var x = n;
                    for (int i = 0; i < length; i++)
                    {
                        buffer[i] = Alphabet[x % Alphabet.Length];
                        x /= Alphabet.Length;
                    }
                    AssertMatches(new string(buffer, 0, length));
                }
            }
        }

        [TestMethod]
        public void CompressSpaces_RandomLongInputs_MatchReference()
        {
            // Long inputs exercise the vectorized search; a low whitespace rate makes long compact runs.
            var random = new Random(12345);
            var sb = new StringBuilder();
            for (int n = 0; n < 20000; n++)
            {
                sb.Clear();
                var length = random.Next(0, 300);
                var wsRate = random.Next(1, 40);
                for (int i = 0; i < length; i++)
                    sb.Append(random.Next(wsRate) == 0 ? Alphabet[random.Next(2, Alphabet.Length)] : (char)('a' + random.Next(26)));
                AssertMatches(sb.ToString());
            }
        }

        [TestMethod]
        public void CompressSpaces_KnownCases_MatchReference()
        {
            foreach (var value in new[]
            {
                null, "", " ", "  ", "\t", "abc", " abc ", "a b c", "a  b", "a\tb", "\tabc", "\tabc  def", " \tabc",
                "  abc", "abc  ", "a \t b", "a\u00A0b", "attempt=1; max=3",
                "aws-sdk-dotnet-coreclr/4.0.0.0 ua/2.0 os/linux lang/.NET_Core#10.0 md/aws-sdk-dotnet-core#4.0.0.0",
                new string('x', 1000) + "  " + new string('y', 10),
                new string('x', 1000) + "\u3000" + new string('y', 10),
            })
            {
                AssertMatches(value);
            }
        }

        private static void AssertMatches(string value)
        {
            foreach (var trim in new[] { false, true })
            {
                Assert.AreEqual(Reference(value, trim), AWSSDKUtils.CompressSpaces(value, trim),
                    $"trim={trim} value=\"{Escape(value)}\"");
            }
        }

        private static string Escape(string value)
        {
            if (value == null)
                return "<null>";
            var sb = new StringBuilder();
            foreach (var c in value)
                sb.Append(c >= 0x20 && c < 0x7F ? c.ToString() : $"\\u{(int)c:X4}");
            return sb.ToString();
        }

        // The implementation of AWSSDKUtils.CompressSpaces before the vectorized fast path was added.
        private static string Reference(string data, bool trim)
        {
            const char SPACE = ' ';
            if (data == null)
                return null;

            var dataLength = data.Length;
            if (dataLength == 0)
                return string.Empty;

            bool prevWasWhiteSpace = false;
            int firstRunIndex = -1;
            int firstNonWhiteSpace = -1;
            for (int i = 0; i < dataLength; i++)
            {
                char c = data[i];
                bool isWS = char.IsWhiteSpace(c);
                if (!isWS && firstNonWhiteSpace < 0)
                    firstNonWhiteSpace = i;

                if (isWS && (prevWasWhiteSpace || c != SPACE))
                {
                    firstRunIndex = i - 1;
                    break;
                }
                prevWasWhiteSpace = isWS;
            }

            if (firstRunIndex < 0)
                return trim ? data.Trim() : data;

            var trimCorrection = trim && firstNonWhiteSpace > 0 ? firstRunIndex - firstNonWhiteSpace : firstRunIndex;

            var span = data.AsSpan();
            if (trim)
                span = span.Trim();

            if (span.Length == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.Append(span.Slice(0, trimCorrection).ToString());
            int pos = trimCorrection;
            while (pos < span.Length)
            {
                if (char.IsWhiteSpace(span[pos]))
                {
                    sb.Append(SPACE);
                    do { pos++; } while (pos < span.Length && char.IsWhiteSpace(span[pos]));
                }
                else
                {
                    int segStart = pos;
                    do { pos++; } while (pos < span.Length && !char.IsWhiteSpace(span[pos]));
                    sb.Append(span.Slice(segStart, pos - segStart).ToString());
                }
            }
            return sb.ToString();
        }
    }
}
