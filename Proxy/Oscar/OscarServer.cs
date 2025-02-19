﻿using System.Text;
using System.Net;
using System.Net.Sockets;
using VintageHive.Proxy.Oscar.Services;
using System.Diagnostics;
using VintageHive.Network;

namespace VintageHive.Proxy.Oscar;

public class OscarServer : Listener
{
    public static readonly List<OscarSession> Sessions = new();

    public static DateTimeOffset ServerTime => DateTime.Now;

    public static readonly Dictionary<ushort, ushort> ServiceVersions = new()
    {
        { OscarGenericServiceControls.FAMILY_ID, 0x03 }, // Generic Service Controls
        { OscarLocationService.FAMILY_ID, 0x01 }, // Location Services
        { OscarBuddyListService.FAMILY_ID, 0x01 }, // Buddy List Management Service
        { OscarIcbmService.FAMILY_ID, 0x01 }, // ICBM (messages) Service
        { 0x05, 0x01 },
        { 0x06, 0x01 },
        { 0x08, 0x01 },
        { OscarPrivacyService.FAMILY_ID, 0x01 }, // (PD) Permit/Deny settings for the user.
        { 0x0A, 0x01 },
        { 0x0B, 0x01 },
        { 0x0C, 0x01 },
        { 0x10, 0x01 },
        { 0x13, 0x01 },
        { OscarIcqService.FAMILY_ID, 0x01 }, // ICQ specific services.
        { OscarAuthorizationService.FAMILY_ID, 0x01 }  // Authorization/Registration Service
    };

    public readonly Flap HelloFlap = new(FlapFrameType.SignOn)
    {
        Data = new byte[] { 0x00, 0x00, 0x00, 0x01 }
    };

    readonly List<IOscarService> _services;

    public OscarServer(IPAddress listenAddress) : base(listenAddress, 5190, SocketType.Stream, ProtocolType.Tcp, false) 
    {
        _services = new()
        {
            new OscarGenericServiceControls(this),
            new OscarLocationService(this),
            new OscarBuddyListService(this),
            new OscarIcbmService(this),
            new OscarPrivacyService(this),
            new OscarIcqService(this),
            new OscarAuthorizationService(this)
        };
    }

    internal override async Task<byte[]> ProcessConnection(ListenerSocket connection)
    {
        var session = new OscarSession(connection);

        Sessions.Add(session);

        await session.SendFlap(HelloFlap);

        session.SentHello = true;

        while (session.Client.IsConnected)
        {
            var flaps = await session.ReceiveFlaps();

            if (flaps == null)
            {
                Thread.Sleep(1);

                continue;
            }

            foreach (Flap flap in flaps)
            {
                switch (flap.Type)
                {
                    case FlapFrameType.SignOn:
                    {
                        if (flap.Data.Length == 4)
                        {
                            // MD5 Style Login

                            continue;
                        }

                        var tlvs = OscarUtils.DecodeTlvs(flap.Data[4..]);

                        if (tlvs.Length != 1)
                        {
                            await ProcessChannelOneAuth(session, tlvs);
                        }
                        else
                        {
                            await ProcessCookieAuth(session, tlvs);
                        }
                    }
                    break;

                    case FlapFrameType.Data:
                    {
                        var snacPacket = flap.GetSnac();

                        Log.WriteLine(Log.LEVEL_INFO, GetType().Name, $"-> {snacPacket}", connection.TraceId.ToString());

                        var familyProcessor = _services.FirstOrDefault(x => x.Family == snacPacket.Family);

                        if (familyProcessor != null)
                        {
                            await familyProcessor.ProcessSnac(session, snacPacket);
                        }
                        else
                        {
                            // REPORT ERROR??
                            // DISCONNECT CLIENT??
                            // Debugger.Break();
                        }
                    }
                    break;

                    case FlapFrameType.SignOff:
                    {
                        var blmService = (OscarBuddyListService)_services.FirstOrDefault(x => x.Family == OscarBuddyListService.FAMILY_ID);

                        await blmService.ProcessOfflineNotifications(session);

                        session.Client.RawSocket.Close();
                    }
                    break;
                }
            }
        }

        Sessions.Remove(session);

        return null;
    }

    private async Task ProcessCookieAuth(OscarSession session, Tlv[] tlvs)
    {
        var cookie = Encoding.ASCII.GetString(tlvs.GetTlv(0x06).Value);

        var storedSession = Mind.Db.OscarGetSessionByCookie(cookie);

        session.Cookie = storedSession.Cookie;
        session.ScreenName = storedSession.ScreenName;
        session.UserAgent = storedSession.UserAgent;

        var genericServiceControls = _services.FirstOrDefault(x => x.Family == OscarGenericServiceControls.FAMILY_ID);

        await genericServiceControls.ProcessSnac(session, new Snac(genericServiceControls.Family, OscarGenericServiceControls.SRV_FAMILIES));
    }

    private static async Task ProcessChannelOneAuth(OscarSession session, Tlv[] tlvs)
    {
        session.ScreenName = Encoding.ASCII.GetString(tlvs.GetTlv(0x01).Value);

        if (OscarUtils.RoastPassword("penis").SequenceEqual(tlvs.GetTlv(0x02).Value))
        {
            session.Cookie = Guid.NewGuid().ToString().ToUpper();

            session.UserAgent = Encoding.ASCII.GetString(tlvs.GetTlv(0x03).Value);

            Mind.Db.OscarSetSession(session);

            var serverIP = ((IPEndPoint)session.Client.RawSocket.LocalEndPoint).Address.MapToIPv4();

            // send yes
            var srvCookie = new List<Tlv>
            {
                new Tlv(0x0001, session.ScreenName),
                new Tlv(0x0005, $"{serverIP}:5190"),
                new Tlv(0x0006, session.Cookie)
            };

            var authSuccessFlap = new Flap(FlapFrameType.SignOff)
            {
                Data = srvCookie.EncodeTlvs()
            };

            await session.SendFlap(authSuccessFlap);

            await session.Client.Stream.FlushAsync();

            session.Client.RawSocket.Close();
        }
        else
        {
            await AuthFailedError(session);
        }
    }

    private static async Task AuthFailedError(OscarSession session)
    {
        var authFailed = new List<Tlv>
        {
            new Tlv(0x0001, session.ScreenName),
            new Tlv(0x0004, "http://hive/aim/help#login"),
            new Tlv(0x0008, (ushort)OscarAuthError.IncorrectScreenNameOrPassword),
            new Tlv(0x000C, 0x0001)
        };

        var authFailedFlap = new Flap(FlapFrameType.SignOff)
        {
            Data = authFailed.EncodeTlvs()
        };

        await session.SendFlap(authFailedFlap);
    }
}
