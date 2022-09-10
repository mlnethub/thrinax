﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections;
using System.Globalization;
using Thrinax.Models;
using Thrinax.Enums;
using Thrinax.Chardet;
using Thrinax.Utility;

namespace Thrinax.Http
{
    public class HttpHelper
    {
        public const string CharsetReg = @"(meta.*?charset=""?(?<Charset>[^\s""'>;]+)""?)|(xml.*?encoding=""?(?<Charset>[^\s"">;]+)""?)";
        private static bool isRegisterGBK = false;

        /// <summary>
        /// 使用Http Request获取网页信息
        /// </summary>
        /// <param name="url">Url</param>
        /// <param name="postData">Post的信息</param>
        /// <param name="cookies">Cookies</param>
        /// <param name="userAgent">浏览器标识</param>
        /// <param name="referer">来源页</param>
        /// <param name="cookiesDomain">Cookies的Domian参数，配合cookies使用；为空则取url的Host</param>
        /// <param name="encode">编码方式，用于解析html</param>
        /// <param name="method">提交方式，例如POST或GET，默认通过postData是否为空判断</param>
        /// <param name="proxy"></param>
        /// <param name="encoding"></param>
        /// <param name="contentType"></param>
        /// <param name="timeout"></param>
        /// <returns></returns>
        public static HttpResult HttpRequest(string url, string postData = null, CookieContainer cookies = null, string userAgent = null, string referer = null, string cookiesDomain = null, Encoding encode = null, string method = null, IWebProxy proxy = null, string encoding = null, string contentType = null, int timeout = 8000, Dictionary<string, string> headers = null)
        {
            HttpResult httpResponse = new HttpResult();

            try
            {
                HttpWebResponse httpWebResponse = null;
                if (!string.IsNullOrEmpty(postData) || (!string.IsNullOrEmpty(method) && method.ToUpper() == "POST"))
                    httpWebResponse = CreatePostHttpResponse(url, postData, timeout, userAgent, cookies, referer, proxy, contentType, headers);
                else
                    httpWebResponse = CreateGetHttpResponse(url, timeout, userAgent, cookies, referer, proxy, contentType, headers);

                httpResponse.Url = httpWebResponse.ResponseUri.ToString();
                httpResponse.HttpCode = (int)httpWebResponse.StatusCode;
                httpResponse.LastModified = TimeUtility.ConvertDateTimeInt(httpWebResponse.LastModified);

                string Content = null;
                //头部预读取缓冲区，字节形式
                var bytes = new List<byte>();
                //头部预读取缓冲区，字符串
                String cache = string.Empty;

                //创建流对象并解码
                Stream ResponseStream;
                switch (httpWebResponse.ContentEncoding?.ToUpperInvariant() ?? "")
                {
                    case "GZIP":
                        ResponseStream = new GZipStream(
                            httpWebResponse.GetResponseStream(), CompressionMode.Decompress);
                        break;
                    case "DEFLATE":
                        ResponseStream = new DeflateStream(
                            httpWebResponse.GetResponseStream(), CompressionMode.Decompress);
                        break;
                    default:
                        ResponseStream = httpWebResponse.GetResponseStream();
                        break;
                }

                if (!isRegisterGBK)
                {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    isRegisterGBK = true;
                }

                try
                {
                    while (true)
                    {
                        var b = ResponseStream.ReadByte();
                        if (b < 0) //end of stream
                            break;
                        bytes.Add((byte)b);

                        if (!cache.EndsWith("</head>", StringComparison.OrdinalIgnoreCase))
                            cache += (char)b;
                    }

                    string Ncharset = "";
                    string Hcharset = "";
                    string Rcharset = "";

                    //1，使用解析ContentType，解析Html编码声明，自动编码识别三种来猜测编码，选取任意两者相同的编码
                    if (encode == null)
                    {
                        Match match = Regex.Match(cache, CharsetReg, RegexOptions.IgnoreCase | RegexOptions.Multiline);
                        if (match.Success)
                            Rcharset = match.Groups["Charset"].Value;

                        try
                        {
                            string text = "";
                            if (!string.IsNullOrEmpty(text = httpWebResponse.ContentType))
                            {
                                text = text.ToLower(CultureInfo.InvariantCulture);
                                string[] array = text.Split(new char[] { ';', '=', ' ' });
                                bool flag = false;
                                string[] array2 = array;
                                for (int i = 0; i < array2.Length; i++)
                                {
                                    string text2 = array2[i];
                                    if (text2 == "charset")
                                        flag = true;
                                    else
                                    {
                                        if (flag)
                                            Hcharset = text2;
                                    }
                                }
                            }

                        }
                        catch { }

                        if (!string.IsNullOrEmpty(Rcharset) && !string.IsNullOrEmpty(Hcharset) && Hcharset.ToUpper() == Rcharset.ToUpper())
                            encode = Encoding.GetEncoding(Hcharset);
                        else
                        {
                            Ncharset = NChardetHelper.RecogCharset(bytes.ToArray(), Language.CHINESE, -1);

                            if (!string.IsNullOrEmpty(Ncharset) && (Ncharset.ToUpper() == Rcharset.ToUpper() || Ncharset.ToUpper() == Hcharset.ToUpper()))
                                encode = Encoding.GetEncoding(Ncharset);
                        }

                    }

                    //2，使用人工标注的编码
                    if (encode == null && !string.IsNullOrEmpty(encoding))
                    {
                        try
                        {
                            encode = Encoding.GetEncoding(encoding);
                        }
                        catch { }
                    }

                    //3，使用单一方式识别出的编码，网页自动识别 > 解析ContentType > 解析Html编码声明
                    if (encode == null && !string.IsNullOrEmpty(Ncharset))
                        encode = Encoding.GetEncoding(Ncharset);
                    if(encode == null && !string.IsNullOrEmpty(Hcharset))
                        encode = Encoding.GetEncoding(Hcharset);
                    if (encode == null && !string.IsNullOrEmpty(Rcharset))
                        encode = Encoding.GetEncoding(Rcharset);

                    //4，使用默认编码，听天由命吧
                    if (encode == null)
                        encode = Encoding.Default;

                    Content = encode.GetString(bytes.ToArray());

                    //get the Cookies，support httponly.
                    if (string.IsNullOrEmpty(cookiesDomain))
                        cookiesDomain = httpWebResponse.ResponseUri?.Host;

                    if (!string.IsNullOrWhiteSpace(cookiesDomain))
                    {
                        cookies = new CookieContainer();
                        CookieCollection httpHeaderCookies = SetCookie(httpWebResponse, cookiesDomain);
                        cookies.Add(httpHeaderCookies ?? httpWebResponse.Cookies);
                    }

                    ResponseStream.Close();
                }
                catch (Exception ex)
                {
                    httpResponse.Content = ex.ToString();
                    return httpResponse;
                }
                finally
                {
                    httpWebResponse.Close();
                }

                httpResponse.Content = Content;
            }
            catch(Exception ex)
            {
                httpResponse.Content = ex.ToString();
                httpResponse.HttpCode = DetermineResultStatus(ex);                
            }
            return httpResponse;
        }

        /// <summary>
        /// 获取网页的内容
        /// </summary>
        /// <param name="url">Url</param>
        /// <param name="postData">Post的信息</param>
        /// <param name="cookies">Cookies</param>
        /// <param name="userAgent">浏览器标识</param>
        /// <param name="referer">来源页</param>
        /// <param name="cookiesDomain">Cookies的Domian参数，配合cookies使用；为空则取url的Host</param>
        /// <param name="encode">编码方式，用于解析html</param>
        /// <param name="method">提交方式，例如POST或GET，默认通过postData是否为空判断</param>
        /// <param name="proxy"></param>
        /// <param name="encoding"></param>
        /// <param name="contentType"></param>
        /// <returns></returns>
        public static string GetHttpContent(string url, string postData = null, CookieContainer cookies = null, string userAgent = null, string referer = null, string cookiesDomain = null, Encoding encode = null, string method = null, IWebProxy proxy = null, string encoding = null, string contentType = null, int timeout = 8000, Dictionary<string, string> headers = null)
        {
            return HttpHelper.HttpRequest(url, postData, cookies, userAgent, referer, cookiesDomain, encode, method, proxy, encoding, contentType, timeout, headers).Content;
        }

        /// <summary>
        /// 创建GET方式的HTTP请求 
        /// </summary>
        /// <param name="url"></param>
        /// <param name="timeout"></param>
        /// <param name="userAgent"></param>
        /// <param name="cookies"></param>
        /// <param name="referer"></param>
        /// <returns></returns>
        public static HttpWebResponse CreateGetHttpResponse(string url, int timeout = 8000, string userAgent = null, CookieContainer cookies = null, string referer = null, IWebProxy proxy = null, string contentType = null, Dictionary<string, string> headers = null)
        {
            HttpWebRequest request = null;
            if (url.StartsWith("https", StringComparison.OrdinalIgnoreCase))
            {
                //对服务端证书进行有效性校验（非第三方权威机构颁发的证书，如自己生成的，不进行验证，这里返回true）
                ServicePointManager.ServerCertificateValidationCallback = new RemoteCertificateValidationCallback(CheckValidationResult);
                request = WebRequest.Create(url) as HttpWebRequest;
                //request.ProtocolVersion = HttpVersion.Version10;    //http版本，默认是1.1,这里设置为1.0
            }
            else
            {
                request = WebRequest.Create(url) as HttpWebRequest;
            }

            request.Referer = referer;
            request.Method = "GET";

            request.Headers.Add(HttpRequestHeader.AcceptEncoding, "gzip,deflate");
            request.Headers.Add(HttpRequestHeader.AcceptLanguage, "zh-CN,zh;q=0.9,en;q=0.8,zh-TW;q=0.7");

            if (headers != null && headers.Count > 0)
            {
                foreach (var header in headers)
                {
                    request.Headers.Add(header.Key, header.Value);
                }
            }

            if (string.IsNullOrEmpty(contentType))
            {
                request.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8";
            }
            else
            {
                request.ContentType = contentType;
            }
            request.UserAgent = string.IsNullOrEmpty(userAgent) ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/63.0.3239.132 Safari/537.36" : userAgent;

            request.Timeout = timeout;
            request.KeepAlive = true;
            request.AllowAutoRedirect = true;

            request.Proxy = proxy;

            if (cookies == null)
                cookies = new CookieContainer();
            request.CookieContainer = cookies;

            return request.GetResponse() as HttpWebResponse;
        }

        /// <summary>
        /// 创建POST方式的HTTP请求
        /// </summary>
        /// <param name="url"></param>
        /// <param name="postData"></param>
        /// <param name="timeout"></param>
        /// <param name="userAgent"></param>
        /// <param name="cookies"></param>
        /// <param name="referer"></param>
        /// <returns></returns>
        public static HttpWebResponse CreatePostHttpResponse(string url, string postData, int timeout = 8000, string userAgent = null, CookieContainer cookies = null, string referer = null, IWebProxy proxy = null, string contentType = null, Dictionary<string,string> headers = null)
        {
            HttpWebRequest request = null;
            //如果是发送HTTPS请求  
            if (url.StartsWith("https", StringComparison.OrdinalIgnoreCase))
            {
                ServicePointManager.ServerCertificateValidationCallback = new RemoteCertificateValidationCallback(CheckValidationResult);
                request = WebRequest.Create(url) as HttpWebRequest;
                //request.ProtocolVersion = HttpVersion.Version10;
            }
            else
            {
                request = WebRequest.Create(url) as HttpWebRequest;
            }
            request.Referer = referer;
            request.Method = "POST";

            request.Headers.Add(HttpRequestHeader.AcceptEncoding, "gzip,deflate");
            request.Headers.Add(HttpRequestHeader.AcceptLanguage, "zh-CN,zh;q=0.9,en;q=0.8,zh-TW;q=0.7");

            if (headers != null && headers.Count > 0)
            {
                foreach (var header in headers)
                {
                    request.Headers.Add(header.Key, header.Value);
                }
            }

            if (string.IsNullOrEmpty(contentType))
            {
                request.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8";
            }
            else
            {
                request.ContentType = contentType;
            }
            request.UserAgent = string.IsNullOrEmpty(userAgent) ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/63.0.3239.132 Safari/537.36" : userAgent;

            request.Timeout = timeout;
            request.KeepAlive = true;
            request.AllowAutoRedirect = true;

            if (cookies == null)
                cookies = new CookieContainer();
            request.CookieContainer = cookies;

            request.Proxy = proxy;

            //发送POST数据  
            if (!string.IsNullOrEmpty(postData))
            {
                byte[] data = Encoding.UTF8.GetBytes(postData);
                request.ContentLength = data.Length;
                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(data, 0, data.Length);
                }
            }
            return request.GetResponse() as HttpWebResponse;
        }

        /// <summary>
        /// 验证证书
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="certificate"></param>
        /// <param name="chain"></param>
        /// <param name="errors"></param>
        /// <returns>是否验证通过</returns>
        private static bool CheckValidationResult(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors)
        {
            if (errors == SslPolicyErrors.None)
                return true;
            return false;
        }

        /// <summary>
        /// 根据response中头部的set-cookie对request中的cookie进行设置
        /// </summary>
        /// <param name="setCookie">The set cookie.</param>
        /// <param name="defaultDomain">The default domain.</param>
        /// <returns></returns>
        private static CookieCollection SetCookie(HttpWebResponse response, string defaultDomain)
        {
            try
            {
                string[] setCookie = response.Headers.GetValues("Set-Cookie");

                // there is bug in it,the datetime in "set-cookie" will be sepreated in two pieces.
                List<string> a = new List<string>(setCookie);
                for (int i = setCookie.Length - 1; i > 0; i--)
                {
                    if (a[i].Substring(a[i].Length - 3) == "GMT")
                    {
                        a[i - 1] = a[i - 1] + ", " + a[i];
                        a.RemoveAt(i);
                        i--;
                    }
                }
                setCookie = a.ToArray<string>();
                CookieCollection cookies = new CookieCollection();
                foreach (string str in setCookie)
                {
                    NameValueCollection hs = new NameValueCollection();
                    foreach (string i in str.Split(';'))
                    {
                        int index = i.IndexOf("=");
                        if (index > 0)
                            hs.Add(i.Substring(0, index).Trim(), i.Substring(index + 1).Trim());
                        else
                            switch (i)
                            {
                                case "HttpOnly":
                                    hs.Add("HttpOnly", "True");
                                    break;
                                case "Secure":
                                    hs.Add("Secure", "True");
                                    break;
                            }
                    }
                    Cookie ck = new Cookie();
                    foreach (string Key in hs.AllKeys)
                    {
                        switch (Key.ToLower().Trim())
                        {
                            case "path":
                                ck.Path = hs[Key];
                                break;
                            case "expires":
                                ck.Expires = DateTime.Parse(hs[Key]);
                                break;
                            case "domain":
                                ck.Domain = hs[Key];
                                break;
                            case "httpOnly":
                                ck.HttpOnly = true;
                                break;
                            case "secure":
                                ck.Secure = true;
                                break;
                            default:
                                ck.Name = Key;
                                ck.Value = hs[Key];
                                break;
                        }
                    }
                    if (ck.Domain == "") ck.Domain = defaultDomain;
                    if (ck.Name != "") cookies.Add(ck);
                }
                return cookies;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Get all cookies from CookieContainer.
        /// </summary>
        /// <param name="cookieContainer">the CookieContainer</param>
        /// <returns>List of cookie</returns>
        public static Dictionary<string, string> GetAllCookies(CookieContainer cookieContainer)
        {
            Dictionary<string, string> cookies = new Dictionary<string, string>();

            Hashtable table = (Hashtable)cookieContainer.GetType().InvokeMember("m_domainTable",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.GetField |
                System.Reflection.BindingFlags.Instance, null, cookieContainer, new object[] { });

            foreach (string pathList in table.Keys)
            {
                StringBuilder _cookie = new StringBuilder();
                SortedList cookieColList = (SortedList)table[pathList].GetType().InvokeMember("m_list",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.GetField
                    | System.Reflection.BindingFlags.Instance, null, table[pathList], new object[] { });
                foreach (CookieCollection colCookies in cookieColList.Values)
                    foreach (Cookie c in colCookies)
                        _cookie.Append(c.Name + "=" + c.Value + ";");

                cookies.Add(pathList, _cookie.ToString().TrimEnd(';'));
            }
            return cookies;
        }

        /// <summary>
        /// convert cookies string to CookieContainer
        /// </summary>
        /// <param name="cookies">cookies dictionary.</param>
        /// <returns>the CookieContainer</returns>
        public static CookieContainer ConvertToCookieContainer(Dictionary<string, string> cookies)
        {
            CookieContainer cookieContainer = new CookieContainer();

            foreach (var cookie in cookies)
            {
                string[] strEachCookParts = cookie.Value.Split(';');
                int intEachCookPartsCount = strEachCookParts.Length;

                foreach (string strCNameAndCValue in strEachCookParts)
                {
                    if (!string.IsNullOrEmpty(strCNameAndCValue))
                    {
                        try
                        {
                            Cookie cookTemp = new Cookie();
                            int firstEqual = strCNameAndCValue.IndexOf("=");
                            string firstName = strCNameAndCValue.Substring(0, firstEqual);
                            string allValue = strCNameAndCValue.Substring(firstEqual + 1, strCNameAndCValue.Length - (firstEqual + 1));
                            cookTemp.Name = firstName;
                            cookTemp.Value = allValue;
                            cookTemp.Path = "/";
                            cookTemp.Domain = cookie.Key;
                            cookieContainer.Add(cookTemp);
                        }
                        catch { }
                    }
                }
            }
            return cookieContainer;
        }

        /// <summary>
        ///     根据异常内容判断状态类型
        /// </summary>
        /// <param name="ex"></param>
        /// <returns></returns>
        private static int DetermineResultStatus(Exception ex)
        {
            string Msg = ex.Message.ToLower();
            if (Msg.Contains("超时") || Msg.Contains("timeout") || Msg.Contains("timed out")
                                   || Msg.Contains("502"))
            {
                return 502;
            }
            if (Msg.Contains("dns") || Msg.Contains("resolved"))
            {
                return 404;
            }
            if (Msg.Contains("404"))
            {
                return 404;
            }
            if (Msg.Contains("500"))
            {
                return 500;
            }

            return 500;
        }
    }
}
