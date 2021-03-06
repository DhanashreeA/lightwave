﻿/*
 * Copyright © 2012-2015 VMware, Inc.  All Rights Reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the “License”); you may not
 * use this file except in compliance with the License.  You may obtain a copy
 * of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an “AS IS” BASIS, without
 * warranties or conditions of any kind, EITHER EXPRESS OR IMPLIED.  See the
 * License for the specific language governing permissions and limitations
 * under the License.
 */

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Vmware.Tools.RestSsoAdminSnapIn.Core.Crypto;
using Vmware.Tools.RestSsoAdminSnapIn.Core.Extensions;
using Vmware.Tools.RestSsoAdminSnapIn.Core.Serialization;
using Vmware.Tools.RestSsoAdminSnapIn.Core.Web;
using Vmware.Tools.RestSsoAdminSnapIn.Dto;

namespace Vmware.Tools.RestSsoAdminSnapIn.Service
{
    public class JwtTokenService : IAuthenticationService
    {
        private readonly IWebRequestManager _webRequestManager;
        public JwtTokenService(IWebRequestManager webRequestManager)
        {
            _webRequestManager = webRequestManager;
        }

        public AuthTokenDto Authenticate(ServerDto serverDto, LoginDto loginDto, string clientId)
        {
            var tenant = Uri.EscapeDataString(loginDto.TenantName);
            var url = ServiceConfigManager.GetLoginUrl(serverDto, tenant);
            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            var data = ServiceConfigManager.FormatLoginArgs(loginDto);
            var requestConfig = new RequestSettings
            {
                Method = HttpMethod.Post,
            };
            var headers = ServiceHelper.AddHeaders();
            var result = _webRequestManager.GetResponse(url, requestConfig, headers, null, data);
            var token = JsonConvert.Deserialize<Token>(result);
            token.Raw = result;
            token.ClientId = clientId;
            token.TokenType = TokenType.Bearer.ToString();
            var certificates = GetCertificates(serverDto, loginDto.TenantName, CertificateScope.TENANT, token);
            var claimsPrincipal = Validate(serverDto, loginDto.User + "@" + loginDto.DomainName, certificates[certificates.Count - 1], loginDto.TenantName, token.IdToken);
            if (claimsPrincipal != null)
                return new AuthTokenDto(Refresh) { Token = token, ClaimsPrincipal = claimsPrincipal, Login = loginDto, ServerDto = serverDto };
            return new AuthTokenDto(Refresh) { Token = token, ClaimsPrincipal = claimsPrincipal, Login = loginDto, ServerDto = serverDto };
        }

        public Token Refresh(ServerDto serverDto, LoginDto loginDto, Token tokenToRefresh)
        {
            var tenant = Uri.EscapeDataString(loginDto.TenantName);
            var url = ServiceConfigManager.GetRefreshUrl(serverDto, tenant);
            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            var data = ServiceConfigManager.FormatRefreshTokenArgs(tokenToRefresh.RefreshToken);
            var requestConfig = new RequestSettings
            {
                Method = HttpMethod.Post,
            };
            var headers = ServiceHelper.AddHeaders();
            var result = _webRequestManager.GetResponse(url, requestConfig, headers, null, data);
            var token = JsonConvert.Deserialize<Token>(result);
            token.RefreshToken = tokenToRefresh.RefreshToken;
            token.ClientId = tokenToRefresh.ClientId;
            return token;
        }

        private ClaimsPrincipal Validate(ServerDto serverDto, string audience, CertificateChainDto certificateChain, string tenantName, string token)
        {
            var certificate = certificateChain.Certificates[0];
            var publicKey = certificate.Encoded;
            var x509Certificate2 = new X509Certificate2();
            var cert = Encoding.UTF8.GetBytes(publicKey);
            x509Certificate2.Import(cert);
            var hostName = ServiceHelper.GetHostName(serverDto.ServerName);
            var validationParams = new TokenValidationParameters
            {
                ValidIssuer = ServiceConfigManager.GetValidIssuer(serverDto, hostName, tenantName),
                ValidAudience = audience,
                IssuerSigningToken = new X509SecurityToken(x509Certificate2),
                ValidateIssuer = false
            };

            var jwtSecurityTokenHandler = new JwtSecurityTokenHandler();
            SecurityToken validatedToken;
            return jwtSecurityTokenHandler.ValidateToken(token, validationParams, out validatedToken);
        }

        private List<CertificateChainDto> GetCertificates(ServerDto serverDto, string tenantName, CertificateScope scope, Token token)
        {
            tenantName = Uri.EscapeDataString(tenantName);
            var url = ServiceConfigManager.GetCertificatesUrl(serverDto, tenantName);
            url += "?scope=" + scope;
            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            var requestConfig = new RequestSettings
            {
                Method = HttpMethod.Get
            };
            var headers = ServiceHelper.AddHeaders(ServiceConfigManager.JsonContentType);
            var authorization = string.Format("{0} {1}", token.TokenType, token.AccessToken);
            headers.Add(HttpRequestHeader.Authorization, authorization);
            var response = _webRequestManager.GetResponse(url, requestConfig, headers, null, null);
            return JsonConvert.Deserialize<List<CertificateChainDto>>(response);
        }

        public AuthTokenDto GetTokenFromCertificate(ServerDto serverDto, X509Certificate2 certificate, RSACryptoServiceProvider rsa)
        {
            var url = ServiceConfigManager.GetTokenFromCertificateUrl(serverDto);
            var signedToken = GetSignedJwtToken(rsa, certificate, url);
            if (signedToken == null)
                throw new Exception("Could not generate a valid token");

            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            var data = ServiceConfigManager.GetJwtTokenBySolutionUserArgs(signedToken);
            var requestConfig = new RequestSettings
            {
                Method = HttpMethod.Post,
            };
            var headers = ServiceHelper.AddHeaders();
            var result = _webRequestManager.GetResponse(url, requestConfig, headers, null, data);
            var token = JsonConvert.Deserialize<Token>(result);
            token.Raw = result;
            return new AuthTokenDto(Refresh) { Token = token, ClaimsPrincipal = null, Login = null, ServerDto = serverDto };
        }
        private string GetSignedJwtToken(RSACryptoServiceProvider rsa, X509Certificate2 cert, string url)
        {
            var claims = new List<Claim>();
            claims.Add(new Claim("token_class", "solution_assertion"));
            claims.Add(new Claim("token_type", "Bearer"));
            claims.Add(new Claim("jti", new Random().Next().ToString()));
            claims.Add(new Claim("sub", cert.Subject));
            var payload = new JwtPayload(cert.Issuer, url, claims, DateTime.Now, DateTime.Now.AddMinutes(5));
            var key = new RsaSecurityKey(rsa);
            var signingCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256Signature, SecurityAlgorithms.Sha256Digest);

            var header = new JwtHeader(signingCredentials);
            var token = new JwtSecurityToken(header, payload);
            var jwtSecurityTokenHandler = new JwtSecurityTokenHandler();
            try
            {                
                var jsonToken = jwtSecurityTokenHandler.WriteToken(token);
                return jsonToken;
            }
            catch (Exception)
            {
                // do nothing
            }
            return null;
        }

        private byte[] GetBytes(string str)
        {
            byte[] bytes = new byte[str.Length * sizeof(char)];
            System.Buffer.BlockCopy(str.ToCharArray(), 0, bytes, 0, bytes.Length);
            return bytes;
        }

        public AuthTokenDto GetTokenFromGssTicket(ServerDto serverDto, string base64EncodedGSSTicketBytes, string clientId)
        {
            var url = ServiceConfigManager.GetTokenFromGssTicketUrl(serverDto);
            ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
            var data = ServiceConfigManager.GetTokenFromGssTicketArgs(base64EncodedGSSTicketBytes, clientId);
            var requestConfig = new RequestSettings
            {
                Method = HttpMethod.Post,
            };
            var headers = ServiceHelper.AddHeaders();
            var result = _webRequestManager.GetResponse(url, requestConfig, headers, null, data);
            var token = JsonConvert.Deserialize<Token>(result);
            token.Raw = result;
            token.ClientId = clientId;
            return new AuthTokenDto(Refresh) { Token = token, ClaimsPrincipal = null, Login = null, ServerDto = serverDto };
        }
    }
}
