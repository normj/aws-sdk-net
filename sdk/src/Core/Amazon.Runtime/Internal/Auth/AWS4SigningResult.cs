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

using Amazon.Util;
using System;
using System.Globalization;
using System.Text;
using ThirdParty.RuntimeBackports;

namespace Amazon.Runtime.Internal.Auth
{
    /// <summary>
    /// Encapsulates the various fields and eventual signing value that makes up 
    /// an AWS4 signature. This can be used to retrieve the required authorization string
    /// or authorization query parameters for the final request as well as hold ongoing
    /// signature computations for subsequent calls related to the initial signing.
    /// </summary>
    public class AWS4SigningResult : AWSSigningResultBase
    {
        private readonly string _awsSecretAccessKey;
        private readonly byte[] _signingKey;
        private readonly byte[] _signature;

        /// <summary>
        /// Constructs a new signing result instance for a computed signature
        /// </summary>
        /// <param name="awsAccessKeyId">The access key that was included in the signature</param>
        /// <param name="awsSecretAccessKey">The secret key that was used to compute the signature</param>
        /// <param name="signedAt">Date/time (UTC) that the signature was computed</param>
        /// <param name="signedHeaders">The collection of headers names that were included in the signature</param>
        /// <param name="scope">Formatted 'scope' value for signing (YYYYMMDD/region/service/aws4_request)</param>
        /// <param name="signingKey">Returns the key that was used to compute the signature</param>
        /// <param name="signature">Computed signature</param>
        public AWS4SigningResult(string awsAccessKeyId,
                                 string  awsSecretAccessKey,
                                 DateTime signedAt,
                                 string signedHeaders,
                                 string scope,
                                 byte[] signingKey,
                                 byte[] signature) :
            base(awsAccessKeyId, signedAt, signedHeaders, scope)
        {
            _awsSecretAccessKey = awsSecretAccessKey;
            _signingKey = signingKey;
            _signature = signature;
        }

        /// <summary>
        /// Returns a copy of the key that was used to compute the signature
        /// </summary>
        public byte[] GetSigningKey()
        {
            var kSigningCopy = new byte[_signingKey.Length];
            _signingKey.CopyTo(kSigningCopy, 0);
            return kSigningCopy;
        }

        /// <summary>
        /// Returns the hex string representing the signature
        /// </summary>
        public override string Signature
        {
            get { return AWSSDKUtils.ToHex(_signature, true); }
        }

        /// <summary>
        /// The secret key that was used to compute the signature
        /// </summary>
        public string SecretKey
        {
            get { return _awsSecretAccessKey; }
        }

        /// <summary>
        /// Returns the signature in a form usable as an 'Authorization' header value.
        /// </summary>
        public override string ForAuthorizationHeader
        {
            get
            {
                // A subclass may override Signature; only this type's own Signature is written directly.
                if (_signature == null || GetType() != typeof(AWS4SigningResult))
                {
                    return $"{AWS4Signer.AWS4AlgorithmTag} {AWS4Signer.Credential}={AccessKeyId}/{Scope}, " +
                           $"{AWS4Signer.SignedHeaders}={SignedHeaders}, {AWS4Signer.Signature}={Signature}";
                }

                var builder = new ValueStringBuilder(stackalloc char[512]);
                builder.Append(AWS4Signer.AWS4AlgorithmTag + " " + AWS4Signer.Credential + "=");
                builder.Append(AccessKeyId);
                builder.Append('/');
                builder.Append(Scope);
                builder.Append(", " + AWS4Signer.SignedHeaders + "=");
                builder.Append(SignedHeaders);
                builder.Append(", " + AWS4Signer.Signature + "=");
                AppendSignatureHex(ref builder);
                return builder.ToString();
            }
        }

        /// <summary>
        /// Appends the same characters as <see cref="Signature"/> without allocating the hex string.
        /// </summary>
        private void AppendSignatureHex(ref ValueStringBuilder builder)
        {
            const string hexDigits = "0123456789abcdef";
            var destination = builder.AppendSpan(_signature.Length * 2);
            for (int i = 0; i < _signature.Length; i++)
            {
                var b = _signature[i];
                destination[2 * i] = hexDigits[b >> 4];
                destination[2 * i + 1] = hexDigits[b & 0xF];
            }
        }

        /// <summary>
        /// Returns the signature in a form usable as a set of query string parameters.
        /// </summary>
        public override string ForQueryParameters
        {
            get
            {
                if (_signature != null && GetType() == typeof(AWS4SigningResult))
                {
                    // Same output as below. The parameter names, the algorithm tag, the '/' (%2F) and the hex
                    // signature are fixed and need no encoding work; the access key and the scope are encoded
                    // separately, which matches encoding them joined by '/' because UTF-8 is encoded per code point
                    // and '/' cannot be part of a surrogate pair.
                    var builder = new ValueStringBuilder(stackalloc char[512]);
                    builder.Append(HeaderKeys.XAmzAlgorithm + "=" + AWS4Signer.AWS4AlgorithmTag + "&" + HeaderKeys.XAmzCredential + "=");
                    builder.Append(AWSSDKUtils.UrlEncode(AccessKeyId, false));
                    builder.Append("%2F");
                    builder.Append(AWSSDKUtils.UrlEncode(Scope, false));
                    builder.Append("&" + HeaderKeys.XAmzDateHeader + "=");
                    builder.Append(AWSSDKUtils.UrlEncode(ISO8601DateTime, false));
                    builder.Append("&" + HeaderKeys.XAmzSignedHeadersHeader + "=");
                    builder.Append(AWSSDKUtils.UrlEncode(SignedHeaders, false));
                    builder.Append("&" + HeaderKeys.XAmzSignature + "=");
                    AppendSignatureHex(ref builder);
                    return builder.ToString();
                }

                var authParams = new StringBuilder()
                    .AppendFormat("{0}={1}", AWSSDKUtils.UrlEncode(HeaderKeys.XAmzAlgorithm, false), AWSSDKUtils.UrlEncode(AWS4Signer.AWS4AlgorithmTag, false))
                    .AppendFormat("&{0}={1}", AWSSDKUtils.UrlEncode(HeaderKeys.XAmzCredential, false), AWSSDKUtils.UrlEncode(string.Format(CultureInfo.InvariantCulture, "{0}/{1}", AccessKeyId, Scope), false))
                    .AppendFormat("&{0}={1}", AWSSDKUtils.UrlEncode(HeaderKeys.XAmzDateHeader, false), AWSSDKUtils.UrlEncode(ISO8601DateTime, false))
                    .AppendFormat("&{0}={1}", AWSSDKUtils.UrlEncode(HeaderKeys.XAmzSignedHeadersHeader, false), AWSSDKUtils.UrlEncode(SignedHeaders, false))
                    .AppendFormat("&{0}={1}", AWSSDKUtils.UrlEncode(HeaderKeys.XAmzSignature, false), AWSSDKUtils.UrlEncode(Signature, false));

                return authParams.ToString();
            }
        }
    }
}
