#if !NETFRAMEWORK
using System;
using System.Collections.Generic;
using System.Text;
using Amazon.Runtime.Internal.Util;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ThirdParty.RuntimeBackports;

namespace AWSSDK.UnitTests
{
    /// <summary>
    /// Pins the exact bytes XMLEncodedBufferTextWriter produces for Write(char[], int, int) and
    /// Write(string), including the order in which an encoder-held lone high surrogate is flushed
    /// around an escape sequence. The reference below is the original character-by-character
    /// algorithm: each clean run between special characters is encoded with flush: false, each
    /// special character is written as its ASCII escape, and Flush encodes with flush: true.
    /// </summary>
    [TestClass]
    [TestCategory("UnitTest")]
    [TestCategory("Runtime")]
    public class XMLEncodedBufferTextWriterSpanTests
    {
        private static readonly char[] Alphabet =
        {
            'a', 'Z', '0', ' ', '<', '&', '"', '\r', '\t', '\n', '\u0085', '\u2028', '\u2029',
            '\u0084', '\u0086', '\u2027', '\u00e9', '\u4e2d', '\uffff', '\ud83d', '\ude00', '\ud800', '\udfff'
        };

        [TestMethod]
        public void EmptyAndNull_WriteNothing()
        {
            AssertMatchesReference(new[] { "" });
            AssertMatchesReference(new[] { "", "", "" });

            using (var buffer = new ArrayPoolBufferWriter<byte>())
            {
                var writer = new XMLEncodedBufferTextWriter(buffer);
                writer.Write((string)null);
                writer.Flush();
                Assert.AreEqual(0, buffer.WrittenCount);
            }
        }

        [TestMethod]
        public void OnlySpecialCharacters()
        {
            AssertMatchesReference(new[] { "\n" });
            AssertMatchesReference(new[] { "\u0085" });
            AssertMatchesReference(new[] { "\u2028" });
            AssertMatchesReference(new[] { "\n\n\u0085\u2028\n" });
        }

        [TestMethod]
        public void SpecialCharactersAtEdges()
        {
            AssertMatchesReference(new[] { "\nabc" });
            AssertMatchesReference(new[] { "abc\n" });
            AssertMatchesReference(new[] { "\u2028abc\u0085" });
            AssertMatchesReference(new[] { "a\nb\u0085c\u2028d" });
        }

        [TestMethod]
        public void NearMissCharacters_AreNotEscaped()
        {
            var bytes = AssertMatchesReference(new[] { "\u0084\u0086\u2027\u2029\r\t\u000b\u000c" });
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("\u0084\u0086\u2027\u2029\r\t\u000b\u000c"), bytes);
        }

        [TestMethod]
        public void LongRuns_AcrossVectorWidths()
        {
            for (int length = 0; length <= 300; length++)
            {
                var clean = new string('x', length);
                AssertMatchesReference(new[] { clean });
                AssertMatchesReference(new[] { clean + "\n" });
                AssertMatchesReference(new[] { "\u2028" + clean });
                AssertMatchesReference(new[] { clean + "\u0085" + clean });
            }
        }

        [TestMethod]
        public void SurrogatePairSplitAcrossWrites()
        {
            AssertMatchesReference(new[] { "a\ud83d", "\ude00b" });
            AssertMatchesReference(new[] { "\ud83d", "\ude00" });
        }

        [TestMethod]
        public void LoneHighSurrogateBeforeEscape()
        {
            // The encoder holds the high surrogate; the escape is written first and the replacement
            // character follows it when the next run is encoded or on Flush.
            AssertMatchesReference(new[] { "a\ud83d\nb" });
            AssertMatchesReference(new[] { "\ud83d\n" });
            AssertMatchesReference(new[] { "\ud83d", "\n", "\ude00" });
            AssertMatchesReference(new[] { "\ud800\u2028\udfff" });
        }

        [TestMethod]
        public void RandomChunks_MatchReference()
        {
            var random = new Random(20261121);
            for (int iteration = 0; iteration < 3000; iteration++)
            {
                int chunkCount = random.Next(1, 5);
                var chunks = new string[chunkCount];
                for (int c = 0; c < chunkCount; c++)
                {
                    int length = random.Next(0, iteration % 10 == 0 ? 2000 : 80);
                    var chars = new char[length];
                    int specialRate = random.Next(1, 40);
                    for (int i = 0; i < length; i++)
                    {
                        chars[i] = random.Next(specialRate) == 0
                            ? Alphabet[random.Next(Alphabet.Length)]
                            : (char)random.Next(0x20, 0x7f);
                    }
                    chunks[c] = new string(chars);
                }
                AssertMatchesReference(chunks);
            }
        }

        [TestMethod]
        public void CharArrayOverload_HonorsIndexAndCount()
        {
            var source = "XX\nab\u2028cd\u0085YY".ToCharArray();
            byte[] actual;
            using (var buffer = new ArrayPoolBufferWriter<byte>())
            {
                var writer = new XMLEncodedBufferTextWriter(buffer);
                writer.Write(source, 2, source.Length - 4);
                writer.Flush();
                actual = buffer.WrittenSpan.ToArray();
            }
            CollectionAssert.AreEqual(Reference(new[] { "\nab\u2028cd\u0085" }), actual);
        }

        // Writes each chunk through both overloads and compares with the reference algorithm.
        private static byte[] AssertMatchesReference(IList<string> chunks)
        {
            var expected = Reference(chunks);

            byte[] viaString;
            using (var buffer = new ArrayPoolBufferWriter<byte>())
            {
                var writer = new XMLEncodedBufferTextWriter(buffer);
                foreach (var chunk in chunks)
                    writer.Write(chunk);
                writer.Flush();
                viaString = buffer.WrittenSpan.ToArray();
            }

            byte[] viaArray;
            using (var buffer = new ArrayPoolBufferWriter<byte>())
            {
                var writer = new XMLEncodedBufferTextWriter(buffer);
                foreach (var chunk in chunks)
                {
                    // Pad the array so index and count are exercised.
                    var padded = ("#" + chunk + "#").ToCharArray();
                    writer.Write(padded, 1, chunk.Length);
                }
                writer.Flush();
                viaArray = buffer.WrittenSpan.ToArray();
            }

            CollectionAssert.AreEqual(expected, viaString, "Write(string) differs for: " + Describe(chunks));
            CollectionAssert.AreEqual(expected, viaArray, "Write(char[], int, int) differs for: " + Describe(chunks));
            return expected;
        }

        private static byte[] Reference(IList<string> chunks)
        {
            var encoder = Encoding.UTF8.GetEncoder();
            var output = new List<byte>();
            foreach (var chunk in chunks)
            {
                int start = 0;
                for (int i = 0; i < chunk.Length; i++)
                {
                    string escape = chunk[i] switch
                    {
                        '\n' => "&#xA;",
                        '\u0085' => "&#x85;",
                        '\u2028' => "&#x2028;",
                        _ => null
                    };
                    if (escape != null)
                    {
                        if (i > start)
                            Encode(encoder, chunk.Substring(start, i - start).ToCharArray(), false, output);
                        output.AddRange(Encoding.ASCII.GetBytes(escape));
                        start = i + 1;
                    }
                }
                if (start < chunk.Length)
                    Encode(encoder, chunk.Substring(start).ToCharArray(), false, output);
            }
            Encode(encoder, Array.Empty<char>(), true, output);
            return output.ToArray();
        }

        private static void Encode(Encoder encoder, char[] chars, bool flush, List<byte> output)
        {
            var bytes = new byte[chars.Length * 3 + 4];
            int count = encoder.GetBytes(chars, 0, chars.Length, bytes, 0, flush);
            for (int i = 0; i < count; i++)
                output.Add(bytes[i]);
        }

        private static string Describe(IList<string> chunks)
        {
            var sb = new StringBuilder();
            foreach (var chunk in chunks)
            {
                sb.Append('[');
                foreach (var c in chunk)
                    sb.Append(c < 0x20 || c > 0x7e ? "\\u" + ((int)c).ToString("x4") : c.ToString());
                sb.Append(']');
            }
            return sb.ToString();
        }
    }
}
#endif
