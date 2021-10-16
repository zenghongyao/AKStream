using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using LibLogger;
using LibCommon;
using LibCommon.Structs;
using LibCommon.Structs.DBModels;
using LibCommon.Structs.GB28181.Net.SIP;
using LibCommon.Structs.GB28181.Sys;
using LibCommon.Structs.GB28181.XML;
using SIPSorcery.SIP;


namespace LibGB28181SipClient
{
    /// <summary>
    /// GB28181客户端类
    /// </summary>
    public class SipClient
    {
        private SIPTransport _sipTransport = null;
        private uint _sn = 0;
        private ushort _cseq = 0;
        private string _registerCallId;
        private string _keepaliveCallId;
        private string _catalogCallId;
        private string _lastCallId;
        private DateTime _keepaliveSendDatetime;
        private bool _isRegister = false;
        private bool _wantAuthorization = false;
        private Thread _registerThread = null;
        private uint _keepaliveLostTimes = 0;
        private DateTime _registerDateTime;
        private DateTime _keeperaliveDateTime;
        private IPEndPoint _localIpEndPoint;
        private IPEndPoint _remoteIpEndPoint;
        private SIPResponse _oldSipResponse;
        private SIPRequest _oldSipRequest;
        private AutoResetEvent _pauseThread = new AutoResetEvent(false);
        private AutoResetEvent _catalogThread = new AutoResetEvent(false);
        public static event GCommon.InviteChannel OnInviteChannel = null!;
        public static event GCommon.DeInviteChannel OnDeInviteChannel = null!;
       

        /// <summary>
        /// 本地端点
        /// </summary>
        public IPEndPoint LocalIpEndPoint
        {
            get => _localIpEndPoint;
            set => _localIpEndPoint = value;
        }

        /// <summary>
        /// 服务器（远程）端点
        /// </summary>
        public IPEndPoint RemoteIpEndPoint
        {
            get => _remoteIpEndPoint;
            set => _remoteIpEndPoint = value;
        }

        /// <summary>
        /// 自增sn
        /// </summary>
        public uint Sn
        {
            get
            {
                _sn++;
                return _sn;
            }
        }

        /// <summary>
        /// 自增seq
        /// </summary>
        public ushort CSeq
        {
            get
            {
                _cseq++;
                return _cseq;
            }
        }

        /// <summary>
        /// 注册事件专用callid
        /// </summary>
        public string RegisterCallid
        {
            get => _registerCallId;
            set => _registerCallId = value;
        }

        /// <summary>
        /// 信令通道实例
        /// </summary>
        public SIPTransport SipClientInstance
        {
            get => _sipTransport;
            set => _sipTransport = value;
        }

      

        /// <summary>
        /// 创建注册事件的验证信令
        /// </summary>
        /// <returns></returns>
        private SIPRequest CreateAuthRegister()
        {
            var realm = _oldSipResponse.Header.AuthenticationHeaders[0].SIPDigest.Realm;
            var nonce = _oldSipResponse.Header.AuthenticationHeaders[0].SIPDigest.Nonce;
            var username = Common.SipClientConfig.SipUsername;
            var password = Common.SipClientConfig.SipPassword;
            SIPProtocolsEnum protocols = SIPProtocolsEnum.udp;
            var toSipUri = new SIPURI(SIPSchemesEnum.sip,
                new SIPEndPoint(protocols, _localIpEndPoint));
            toSipUri.User = Common.SipClientConfig.SipServerDeviceId;
            SIPToHeader to = new SIPToHeader(null, toSipUri, null);
            var fromSipUri = new SIPURI(SIPSchemesEnum.sip, _localIpEndPoint.Address, _localIpEndPoint.Port);
            fromSipUri.User = Common.SipClientConfig.SipDeviceId;
            SIPFromHeader from = new SIPFromHeader(null, fromSipUri, "AKStreamClient");
            SIPRequest req = SIPRequest.GetRequest(SIPMethodsEnum.REGISTER, toSipUri, to,
                from,
                new SIPEndPoint(protocols,
                    _localIpEndPoint));
            req.Header.Allow = null;
            req.Header.Contact = new List<SIPContactHeader>()
            {
                new SIPContactHeader(null, fromSipUri)
            };
            req.Header.Expires = 3600;
            req.Header.UserAgent = "AKStreamSipClient/1.0";
            req.Header.CallId = _registerCallId;
            RegisterCallid = req.Header.CallId;
            req.Header.CSeq = CSeq;
            var HA1 = LibCommon.UtilsHelper.Md5($"{username}:{realm}:{password}");
            var HA2 = LibCommon.UtilsHelper.Md5($"REGISTER:{fromSipUri}");
            var response = UtilsHelper.Md5($"{HA1}:{nonce}:{HA2}");
            req.Header.AuthenticationHeaders = new List<SIPAuthenticationHeader>();
            var authHeader = new SIPAuthenticationHeader(
                new SIPAuthorisationDigest(SIPAuthorisationHeadersEnum.WWWAuthenticate)
                {
                    Username = username,
                    Response = response,
                    Realm = realm,
                    Nonce = nonce,
                    URI = fromSipUri.ToString(),
                });
            req.Header.AuthenticationHeaders.Add(authHeader);
            return req;
        }

        /// <summary>
        /// 创建第一次注册信令
        /// </summary>
        /// <returns></returns>
        private SIPRequest CreateFirstRegister()
        {
            SIPProtocolsEnum protocols = SIPProtocolsEnum.udp;
            var toSipUri = new SIPURI(SIPSchemesEnum.sip,
                new SIPEndPoint(protocols, _localIpEndPoint));
            toSipUri.User = Common.SipClientConfig.SipServerDeviceId;
            SIPToHeader to = new SIPToHeader(null, toSipUri, null);

            var fromSipUri = new SIPURI(SIPSchemesEnum.sip, _localIpEndPoint.Address, _localIpEndPoint.Port);
            fromSipUri.User = Common.SipClientConfig.SipDeviceId;
            SIPFromHeader from = new SIPFromHeader(null, fromSipUri, "AKStreamClient");
            SIPRequest req = SIPRequest.GetRequest(SIPMethodsEnum.REGISTER, toSipUri, to,
                from,
                new SIPEndPoint(protocols,
                    _localIpEndPoint));
            req.Header.Allow = null;
            req.Header.Contact = new List<SIPContactHeader>()
            {
                new SIPContactHeader(null, fromSipUri)
            };
            req.Header.Expires = 3600;
            req.Header.UserAgent = "AKStreamSipClient/1.0";
            req.Header.CallId = CallProperties.CreateNewCallId();
            RegisterCallid = req.Header.CallId;
            req.Header.CSeq = CSeq;
            return req;
        }

        /// <summary>
        /// 创建心跳信令
        /// </summary>
        /// <returns></returns>
        private SIPRequest CreateKeepAlive()
        {
            SIPProtocolsEnum protocols = SIPProtocolsEnum.udp;
            var toSipUri = new SIPURI(SIPSchemesEnum.sip,
                new SIPEndPoint(protocols, _localIpEndPoint));
            toSipUri.User = Common.SipClientConfig.SipServerDeviceId;
            SIPToHeader to = new SIPToHeader(null, toSipUri, null);

            var fromSipUri = new SIPURI(SIPSchemesEnum.sip, _localIpEndPoint.Address, _localIpEndPoint.Port);
            fromSipUri.User = Common.SipClientConfig.SipDeviceId;
            SIPFromHeader from = new SIPFromHeader(null, fromSipUri, "AKStreamClient");
            SIPRequest req = SIPRequest.GetRequest(SIPMethodsEnum.MESSAGE, toSipUri, to,
                from,
                new SIPEndPoint(protocols,
                    _localIpEndPoint));
            req.Header.Allow = null;
            req.Header.Contact = new List<SIPContactHeader>()
            {
                new SIPContactHeader(null, fromSipUri)
            };
            req.Header.UserAgent = "AKStreamSipClient/1.0";
            req.Header.ContentType = "Application/MANSCDP+xml";
            req.Header.CallId = CallProperties.CreateNewCallId();
            _keepaliveCallId = req.Header.CallId;
            req.Header.CSeq = CSeq;
            var keepaliveBody = new KeepAlive();
            keepaliveBody.Status = "OK";
            keepaliveBody.CmdType = CommandType.Keepalive;
            keepaliveBody.SN = (int)Sn;
            keepaliveBody.DeviceID = Common.SipClientConfig.SipDeviceId;
            req.Body = KeepAlive.Instance.Save<KeepAlive>(keepaliveBody);
            return req;
        }

        /// <summary>
        /// 心跳保持
        /// </summary>
        private void KeeperAlive()
        {
            while ((DateTime.Now - _registerDateTime).TotalSeconds < Common.SipClientConfig.Expiry && _isRegister)
            {
                if ((DateTime.Now - _keeperaliveDateTime).TotalSeconds > Common.SipClientConfig.KeepAliveInterval + 2)
                {
                    _keepaliveLostTimes++;
                }

                if (_keepaliveLostTimes > Common.SipClientConfig.KeepAliveLostNumber)
                {
                    Logger.Warn(
                        $"[{Common.LoggerHead}]->与Sip服务器的心跳丢失超过{Common.SipClientConfig.KeepAliveLostNumber}次->系统重新进入设备注册模式");
                    _isRegister = false;
                    _wantAuthorization = false;
                    _keepaliveCallId = "";
                    _pauseThread.Close();
                    _catalogThread.Close();
                    _catalogCallId = "";
                    _keepaliveLostTimes = 0;
                    _lastCallId = "";
                    _oldSipRequest = null;
                    _oldSipResponse = null;
                    _registerCallId = "";
                    _pauseThread.Reset();
                    _registerThread.Abort();
                    _registerThread = new Thread(Register);
                    _registerThread.Start();
                    break;
                }

                var req = CreateKeepAlive();
                SipClientInstance.SendRequestAsync(
                    new SIPEndPoint(SIPProtocolsEnum.udp, _remoteIpEndPoint.Address, _remoteIpEndPoint.Port),
                    req);
                _oldSipRequest = req;
                Logger.Debug(
                    $"[{Common.LoggerHead}]->发送心跳数据->{req.RemoteSIPEndPoint}->{req}");
                _keepaliveSendDatetime = DateTime.Now;
                Thread.Sleep(Common.SipClientConfig.KeepAliveInterval * 1000);
            }

            if ((DateTime.Now - _registerDateTime).TotalSeconds > Common.SipClientConfig.Expiry)
            {
                Logger.Warn(
                    $"[{Common.LoggerHead}]->超过注册有效期:{Common.SipClientConfig.Expiry}->系统重新进入设备注册模式");

                _isRegister = false;
                _wantAuthorization = false;
                _keepaliveCallId = "";
                _pauseThread.Close();
                _catalogThread.Close();
                _catalogCallId = "";
                _keepaliveLostTimes = 0;
                _lastCallId = "";
                _oldSipRequest = null;
                _oldSipResponse = null;
                _registerCallId = "";
                _pauseThread.Reset();
                _registerThread.Abort();
                _registerThread = new Thread(Register);
                _registerThread.Start();
            }
        }


        /// <summary>
        /// 注册保持 
        /// </summary>
        private void Register()
        {
            while (!_isRegister)
            {
                if (!_isRegister && !_wantAuthorization)
                {
                    var req = CreateFirstRegister();
                    _oldSipRequest = req;
                    SipClientInstance.SendRequestAsync(
                        new SIPEndPoint(SIPProtocolsEnum.udp, _remoteIpEndPoint.Address, _remoteIpEndPoint.Port),
                        req);
                    Logger.Debug(
                        $"[{Common.LoggerHead}]->发送首次注册数据->{req.RemoteSIPEndPoint}->{req}");
                }
                else if (!_isRegister && _wantAuthorization)
                {
                    var req = CreateAuthRegister();
                    _oldSipRequest = req;
                    SipClientInstance.SendRequestAsync(
                        new SIPEndPoint(SIPProtocolsEnum.udp, _remoteIpEndPoint.Address, _remoteIpEndPoint.Port),
                        req);
                    Logger.Debug(
                        $"[{Common.LoggerHead}]->发送验证注册数据->{req.RemoteSIPEndPoint}->{req}");
                }

                _pauseThread.WaitOne(Common.SipClientConfig.KeepAliveInterval * 1000);
            }
        }


        /// <summary>
        /// 发送回复确认
        /// </summary>
        /// <param name="sipRequest"></param>
        private async Task SendOkMessage(SIPRequest sipRequest)
        {
            SIPResponseStatusCodesEnum messaageResponse = SIPResponseStatusCodesEnum.Ok;
            SIPResponse okResponse = SIPResponse.GetResponse(sipRequest, messaageResponse, null);
            await _sipTransport.SendResponseAsync(okResponse);
            Logger.Debug(
                $"[{Common.LoggerHead}]->发送确认数据->{okResponse.RemoteSIPEndPoint}->{okResponse}");
        }
        
      

        /// <summary>
        /// 生成设备信息信令
        /// </summary>
        /// <returns></returns>
        private SIPRequest CreateDeviceInfo()
        {
            SIPProtocolsEnum protocols = SIPProtocolsEnum.udp;
            var toSipUri = new SIPURI(SIPSchemesEnum.sip,
                new SIPEndPoint(protocols, _localIpEndPoint));
            toSipUri.User = Common.SipClientConfig.SipServerDeviceId;
            SIPToHeader to = new SIPToHeader(null, toSipUri, null);

            var fromSipUri = new SIPURI(SIPSchemesEnum.sip, _localIpEndPoint.Address, _localIpEndPoint.Port);
            fromSipUri.User = Common.SipClientConfig.SipDeviceId;
            SIPFromHeader from = new SIPFromHeader(null, fromSipUri, "AKStreamClient");
            SIPRequest req = SIPRequest.GetRequest(SIPMethodsEnum.MESSAGE, toSipUri, to,
                from,
                new SIPEndPoint(protocols,
                    _localIpEndPoint));
            req.Header.Allow = null;
            req.Header.Contact = new List<SIPContactHeader>()
            {
                new SIPContactHeader(null, fromSipUri)
            };
            req.Header.UserAgent = "AKStreamSipClient/1.0";
            req.Header.ContentType = "Application/MANSCDP+xml";
            req.Header.CallId = CallProperties.CreateNewCallId();
            _lastCallId = req.Header.CallId;
            req.Header.CSeq = CSeq;
            var deviceInfoBody = new DeviceInfo();
            deviceInfoBody.CmdType = CommandType.DeviceInfo;
            deviceInfoBody.Manufacturer = "AKStream";
            deviceInfoBody.Firmware = "V1.0";
            deviceInfoBody.Model = "AKStreamWeb V1.0";
            deviceInfoBody.Result = "OK";
            deviceInfoBody.DeviceID = Common.SipClientConfig.SipDeviceId;
            deviceInfoBody.DeviceName = "AKStream SipClient";
            deviceInfoBody.SN = (int)Sn;
            ResponseStruct rs;
            var ret = WebApiHelper.ShareChannelSumCount(out rs);
            if (ret > -1 && rs.Code.Equals(ErrorNumber.None))
            {
                deviceInfoBody.Channel = ret;
            }

            req.Body = DeviceInfo.Instance.Save<DeviceInfo>(deviceInfoBody);
            return req;
        }

        /// <summary>
        /// 处理设备信息
        /// </summary>
        private async Task ProcessDeviceInfo()
        {
            var req = CreateDeviceInfo();
            await SipClientInstance.SendRequestAsync(
                new SIPEndPoint(SIPProtocolsEnum.udp, _remoteIpEndPoint.Address, _remoteIpEndPoint.Port),
                req);
            Logger.Debug(
                $"[{Common.LoggerHead}]->发送设备信息数据->{req.RemoteSIPEndPoint}->{req}");
            _oldSipRequest = req;
        }

        /// <summary>
        /// 生成设备状态信令
        /// </summary>
        /// <returns></returns>
        private SIPRequest CreateDeviceStatus()
        {
            SIPProtocolsEnum protocols = SIPProtocolsEnum.udp;
            var toSipUri = new SIPURI(SIPSchemesEnum.sip,
                new SIPEndPoint(protocols, _localIpEndPoint));
            toSipUri.User = Common.SipClientConfig.SipServerDeviceId;
            SIPToHeader to = new SIPToHeader(null, toSipUri, null);

            var fromSipUri = new SIPURI(SIPSchemesEnum.sip, _localIpEndPoint.Address, _localIpEndPoint.Port);
            fromSipUri.User = Common.SipClientConfig.SipDeviceId;
            SIPFromHeader from = new SIPFromHeader(null, fromSipUri, "AKStreamClient");
            SIPRequest req = SIPRequest.GetRequest(SIPMethodsEnum.MESSAGE, toSipUri, to,
                from,
                new SIPEndPoint(protocols,
                    _localIpEndPoint));
            req.Header.Allow = null;
            req.Header.Contact = new List<SIPContactHeader>()
            {
                new SIPContactHeader(null, fromSipUri)
            };
            req.Header.UserAgent = "AKStreamSipClient/1.0";
            req.Header.ContentType = "Application/MANSCDP+xml";
            req.Header.CallId = CallProperties.CreateNewCallId();
            _lastCallId = req.Header.CallId;
            req.Header.CSeq = CSeq;
            var deviceStatusBody = new DeviceStatus();
            deviceStatusBody.CmdType = CommandType.DeviceStatus;
            deviceStatusBody.Alarmstatus = new Alarmstatus();
            deviceStatusBody.Online = "ONLINE";
            deviceStatusBody.DeviceID = Common.SipClientConfig.SipDeviceId;
            deviceStatusBody.Result = "OK";
            deviceStatusBody.Status = "OK";
            deviceStatusBody.DeviceTime = DateTime.Now;
            deviceStatusBody.Record = "OFF";
            deviceStatusBody.SN = (int)Sn;
            req.Body = DeviceStatus.Instance.Save<DeviceStatus>(deviceStatusBody);
            return req;
        }

        /// <summary>
        /// 处理设备状态
        /// </summary>
        private async Task ProcessDeviceStatus()
        {
            var req = CreateDeviceStatus();
            await SipClientInstance.SendRequestAsync(
                new SIPEndPoint(SIPProtocolsEnum.udp, _remoteIpEndPoint.Address, _remoteIpEndPoint.Port),
                req);
            Logger.Debug(
                $"[{Common.LoggerHead}]->发送设备状态数据->{req.RemoteSIPEndPoint}->{req}");
            _oldSipRequest = req;
        }


        /// <summary>
        /// 按指定数量对List分组
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="list"></param>
        /// <param name="groupNum"></param>
        /// <returns></returns>
        private List<List<T>> GetListGroup<T>(List<T> list, int groupNum)
        {
            List<List<T>> listGroup = new List<List<T>>();
            for (int i = 0; i < list.Count(); i += groupNum)
            {
                listGroup.Add(list.Skip(i).Take(groupNum).ToList());
            }

            return listGroup;
        }

        /// <summary>
        /// 生成设备目录信令
        /// </summary>
        /// <param name="tmpList"></param>
        /// <param name="total"></param>
        /// <returns></returns>
        private SIPRequest CreateCatalog(List<VideoChannel> tmpList, int total)
        {
            SIPProtocolsEnum protocols = SIPProtocolsEnum.udp;
            var toSipUri = new SIPURI(SIPSchemesEnum.sip,
                new SIPEndPoint(protocols, _localIpEndPoint));
            toSipUri.User = Common.SipClientConfig.SipServerDeviceId;
            SIPToHeader to = new SIPToHeader(null, toSipUri, null);

            var fromSipUri = new SIPURI(SIPSchemesEnum.sip, _localIpEndPoint.Address, _localIpEndPoint.Port);
            fromSipUri.User = Common.SipClientConfig.SipDeviceId;
            SIPFromHeader from = new SIPFromHeader(null, fromSipUri, "AKStreamClient");
            SIPRequest req = SIPRequest.GetRequest(SIPMethodsEnum.MESSAGE, toSipUri, to,
                from,
                new SIPEndPoint(protocols,
                    _localIpEndPoint));
            req.Header.Allow = null;
            req.Header.Contact = new List<SIPContactHeader>()
            {
                new SIPContactHeader(null, fromSipUri)
            };
            req.Header.UserAgent = "AKStreamSipClient/1.0";
            req.Header.ContentType = "Application/MANSCDP+xml";
            req.Header.CallId =
                string.IsNullOrEmpty(_catalogCallId) ? CallProperties.CreateNewCallId() : _catalogCallId;
            _catalogCallId = req.Header.CallId;
            req.Header.CSeq = CSeq;
            var catalogBody = new Catalog();
            catalogBody.CmdType = CommandType.Catalog;
            catalogBody.SumNum = total;
            catalogBody.SN = (int)Sn;
            catalogBody.DeviceID = Common.SipClientConfig.SipDeviceId;
            catalogBody.DeviceList = new Catalog.DevList();
            foreach (var obj in tmpList)
            {
                var devItem = new Catalog.Item();
                devItem.Manufacturer = "AKStream";
                devItem.Name = obj.ChannelName;
                devItem.Model = "AKStream";
                devItem.Owner = "Owner";
                devItem.CivilCode = "CivilCode";
                devItem.IPAddress = Common.SipClientConfig.LocalIpAddress;
                devItem.Parental = 0;
                devItem.SafetyWay = 0;
                devItem.RegisterWay = 1;
                devItem.Status = DevStatus.ON;
                devItem.Secrecy = 0;
                devItem.DeviceID = obj.ShareDeviceId;
                catalogBody.DeviceList.Items.Add(devItem);
            }

            req.Body = Catalog.Instance.Save<Catalog>(catalogBody);
            return req;
        }

        /// <summary>
        /// 处理设备目录
        /// </summary>
        private async Task ProcessCatalog()
        {
            try
            {
                ResponseStruct rs;
                var shareList = WebApiHelper.GetShareChannelList(out rs);
                if (shareList != null && rs.Code.Equals(ErrorNumber.None))
                {
                    var listGroup = GetListGroup(shareList, 2);
                    foreach (var obj in listGroup)
                    {
                        var req = CreateCatalog(obj, shareList.Count);
                        await SipClientInstance.SendRequestAsync(
                            new SIPEndPoint(SIPProtocolsEnum.udp, _remoteIpEndPoint.Address, _remoteIpEndPoint.Port),
                            req);
                        Logger.Debug(
                            $"[{Common.LoggerHead}]->发送目录查询结果数据->{req.RemoteSIPEndPoint}->{req}");
                        _oldSipRequest = req;
                        _catalogThread.WaitOne(5000);
                    }

                    _catalogCallId = ""; //这里要置空
                }
            }
            catch
            {
            }
        }


        /// <summary>
        /// 获取流共享信息
        /// </summary>
        /// <param name="req"></param>
        /// <returns></returns>
        private ShareInviteInfo GetShareInfo(SIPRequest req)
        {
            var sdpBody = req.Body;

            try
            {
                string mediaip = "";
                ushort mediaport = 0;
                string ssrc = "";
                string channelid =
                    
                    req.Header.Subject.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries)[
                        0];
                channelid = channelid.Substring(0, channelid.IndexOf(':'));
                Console.WriteLine(channelid);

                string[] sdpBodys = sdpBody.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                if (sdpBodys.Length == 0)
                {
                    sdpBodys = sdpBody.Split("\n", StringSplitOptions.RemoveEmptyEntries);

                }

                if (sdpBodys.Length == 0)
                {
                    sdpBodys = sdpBody.Split("\r", StringSplitOptions.RemoveEmptyEntries);
                }

                foreach (var line in sdpBodys)
                {
                    if (line.Trim().ToLower().StartsWith("o="))
                    {
                        var tmp = line.ToLower().Split("ip4", StringSplitOptions.RemoveEmptyEntries);
                        if (tmp.Length == 2)
                        {
                            mediaip = tmp[1];
                        }
                        else
                        {
                            return null;
                        }
                    }

                    if (line.Trim().ToLower().StartsWith("m=video"))
                    {
                        mediaport = ushort.Parse(UtilsHelper.GetValue(line.ToLower(), "m\\=video", "rtp").Trim());
                    }

                    if (line.Trim().ToLower().StartsWith("y="))
                    {
                        var tmp2 = line.Split("=", StringSplitOptions.RemoveEmptyEntries);
                        if (tmp2.Length == 2)
                        {
                            ssrc = tmp2[1];
                        }
                        else
                        {
                            return null;
                        }
                    }
                }
                
                return new ShareInviteInfo()
                {
                    ChannelId = channelid,
                    RemoteIpAddress = mediaip,
                    RemotePort = mediaport,
                    Ssrc = ssrc,
                    CallId = req.Header.CallId,
                    Cseq = req.Header.CSeq,
                };

            }
            catch
            {
                return null;
            }

            return null;
        }

        /// <summary>
        /// 处理来自远端的请求
        /// </summary>
        /// <param name="localSipChannel"></param>
        /// <param name="localSIPEndPoint"></param>
        /// <param name="remoteEndPoint"></param>
        /// <param name="sipRequest"></param>
        private async Task RecvSipMessageOfRequest(SIPChannel localSipChannel, SIPEndPoint localSIPEndPoint,
            SIPEndPoint remoteEndPoint,
            SIPRequest sipRequest)
        {
            var method = sipRequest.Header.CSeqMethod;
            switch (method)
            {
                case SIPMethodsEnum.BYE:
                    Console.WriteLine(sipRequest);
                    OnDeInviteChannel?.Invoke(sipRequest);
                    break;
                case SIPMethodsEnum.INVITE:
                    var shareinfo = GetShareInfo(sipRequest);
                    if (shareinfo != null)
                    {
                        SIPResponse tryingResponse = SIPResponse.GetResponse(sipRequest, SIPResponseStatusCodesEnum.Trying, null);
                        await SipClientInstance.SendResponseAsync(tryingResponse);
                        var b = OnInviteChannel?.Invoke(shareinfo);
                        if (b==true)
                        {
                            
                        }
                    }
                      
                 
                    break;
                case SIPMethodsEnum.MESSAGE:
                    if (sipRequest.Header.ContentType.Equals(ConstString.Application_MANSCDP))
                    {
                        XElement bodyXml = XElement.Parse(sipRequest.Body);
                        string cmdType = bodyXml.Element("CmdType")?.Value.ToUpper()!;
                        switch (cmdType)
                        {
                            case "DEVICEINFO":
                                //查询设备信息
                                Logger.Debug(
                                    $"[{Common.LoggerHead}]->收到设备信息查询信令->{sipRequest.RemoteSIPEndPoint}->{sipRequest}");
                                await SendOkMessage(sipRequest);
                                await ProcessDeviceInfo();
                                break;
                            case "DEVICESTATUS":
                                //查询设备状态
                                Logger.Debug(
                                    $"[{Common.LoggerHead}]->收到设备状态查询信令->{sipRequest.RemoteSIPEndPoint}->{sipRequest}");
                                await SendOkMessage(sipRequest);
                                await ProcessDeviceStatus();
                                break;
                            case "CATALOG":
                                Logger.Debug(
                                    $"[{Common.LoggerHead}]->收到设备目录查询信令->{sipRequest.RemoteSIPEndPoint}->{sipRequest}");
                                await SendOkMessage(sipRequest);
                                Task.Run(() => { ProcessCatalog(); }); //抛线程出去处理

                                break;
                        }
                    }

                    break;
            }
        }

        /// <summary>
        /// 处理来自远端的回复
        /// </summary>
        /// <param name="localSipChannel"></param>
        /// <param name="localSIPEndPoint"></param>
        /// <param name="remoteEndPoint"></param>
        /// <param name="sipResponse"></param>
        /// <returns></returns>
        private Task RecvSipMessageOfResponse(SIPChannel localSipChannel, SIPEndPoint localSIPEndPoint,
            SIPEndPoint remoteEndPoint,
            SIPResponse sipResponse)
        {
            var method = sipResponse.Header.CSeqMethod;
            var status = sipResponse.Status;
            switch (method)
            {
                case SIPMethodsEnum.MESSAGE:

                    if (sipResponse.Header.CallId.Equals(_keepaliveCallId))
                    {
                        if (status == SIPResponseStatusCodesEnum.Ok)
                        {
                            Logger.Debug(
                                $"[{Common.LoggerHead}]->收到设备心跳确认回复->{sipResponse.RemoteSIPEndPoint}->{sipResponse}");
                            _keeperaliveDateTime = DateTime.Now;
                        }
                        else if (status == SIPResponseStatusCodesEnum.BadRequest)
                        {
                            Logger.Debug(
                                $"[{Common.LoggerHead}]->收到设备心跳异常回复->{sipResponse.RemoteSIPEndPoint}->{sipResponse}");
                            _isRegister = false;
                            _wantAuthorization = false;
                            _keepaliveCallId = "";
                            _pauseThread.Close();
                            _catalogThread.Close();
                            _catalogCallId = "";
                            _keepaliveLostTimes = 0;
                            _lastCallId = "";
                            _oldSipRequest = null;
                            _oldSipResponse = null;
                            _registerCallId = "";

                            _pauseThread.Reset();
                            _registerThread.Abort();
                            _registerThread = new Thread(Register);
                            _registerThread.Start();
                        }
                    }

                    if (sipResponse.Header.CallId.Equals(_lastCallId))
                    {
                        Logger.Debug(
                            $"[{Common.LoggerHead}]->收到设备信息确认回复->{sipResponse.RemoteSIPEndPoint}->{sipResponse}");
                    }

                    if (sipResponse.Header.CallId.Equals(_catalogCallId))
                    {
                        Logger.Debug(
                            $"[{Common.LoggerHead}]->收到设备目录确认回复->{sipResponse.RemoteSIPEndPoint}->{sipResponse}");

                        _catalogThread.Set();
                    }

                    break;
                case SIPMethodsEnum.REGISTER:

                    if (status == SIPResponseStatusCodesEnum.Unauthorised ||
                        status == SIPResponseStatusCodesEnum.Unauthorized)
                    {
                        Logger.Debug(
                            $"[{Common.LoggerHead}]->收到要求注册验证回复->{sipResponse.RemoteSIPEndPoint}->{sipResponse}");

                        _wantAuthorization = true;
                        _oldSipResponse = sipResponse;
                        _registerCallId = _oldSipResponse.Header.CallId;
                        _pauseThread.Set();
                    }
                    else if (status == SIPResponseStatusCodesEnum.Ok)
                    {
                        if (sipResponse.Header.CallId.Equals(_registerCallId))
                        {
                            Logger.Debug(
                                $"[{Common.LoggerHead}]->收到注册完成回复->{sipResponse.RemoteSIPEndPoint}->{sipResponse}");

                            _wantAuthorization = false;
                            _isRegister = true;
                            _oldSipResponse = sipResponse;
                            _registerDateTime = DateTime.Now;
                            Common.SipClientConfig.Expiry = (ushort)sipResponse.Header.Expires;
                            _pauseThread.Close();
                            new Thread(new ThreadStart(delegate
                            {
                                try
                                {
                                    KeeperAlive();
                                }
                                catch
                                {
                                }
                            })).Start();
                        }
                    }

                    break;
            }

            return null;
        }

        /// <summary>
        /// 类构造
        /// </summary>
        /// <exception cref="AkStreamException"></exception>
        public SipClient()
        {
            ResponseStruct rs;
            var ret = Common.ReadSipClientConfig(out rs);
            if (ret < 0 || !rs.Code.Equals(ErrorNumber.None))
            {
                Logger.Error($"[{Common.LoggerHead}]->加载配置文件失败->{Common.SipClientConfigPath}");
                throw new AkStreamException(rs);
            }


            Common.SipClient = this;
            Logger.Info($"[{Common.LoggerHead}]->加载配置文件成功->{Common.SipClientConfigPath}");

            try
            {
                _sipTransport = new SIPTransport();
                _sipTransport.SIPTransportResponseReceived += RecvSipMessageOfResponse;
                _sipTransport.SIPTransportRequestReceived += RecvSipMessageOfRequest;
                _localIpEndPoint = new IPEndPoint(IPAddress.Parse(Common.SipClientConfig.LocalIpAddress),
                    Common.SipClientConfig.LocalPort);
                _remoteIpEndPoint = new IPEndPoint(IPAddress.Parse(Common.SipClientConfig.SipServerIpAddress),
                    Common.SipClientConfig.SipServerPort);
                if (Common.SipClientConfig.Enable)
                {
                    _sipTransport.AddSIPChannel(new SIPUDPChannel(
                        IPAddress.Any, Common.SipClientConfig.LocalPort));
                    _registerThread = new Thread(Register);
                    _registerThread.Start();
                }
                else
                {
                    Logger.Info(
                        $"[{Common.LoggerHead}]->Sip客户端未开启->SipClientConfig.Enable={Common.SipClientConfig.Enable}");
                }
            }
            catch (Exception ex)
            {
                rs = new ResponseStruct()
                {
                    Code = ErrorNumber.SipClient_InitExcept,
                    Message = ErrorMessage.ErrorDic![ErrorNumber.SipClient_InitExcept],
                    ExceptMessage = ex.Message,
                    ExceptStackTrace = ex.StackTrace,
                };
                Logger.Error($"[{Common.LoggerHead}]->初始化SipClient异常->{JsonHelper.ToJson(rs)}");
            }
        }
    }
}