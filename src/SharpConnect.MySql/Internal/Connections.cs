﻿//LICENSE: MIT
//Copyright(c) 2012 Felix Geisendörfer(felix @debuggable.com) and contributors 
//Copyright(c) 2015 brezza92, EngineKit and contributors

//Permission is hereby granted, free of charge, to any person obtaining a copy
//of this software and associated documentation files (the "Software"), to deal
//in the Software without restriction, including without limitation the rights
//to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//copies of the Software, and to permit persons to whom the Software is
//furnished to do so, subject to the following conditions:

//The above copyright notice and this permission notice shall be included in
//all copies or substantial portions of the Software.

//THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
//THE SOFTWARE.

using System;
using System.IO;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

using SharpConnect.Internal;

namespace SharpConnect.MySql.Internal
{
    static class dbugConsole
    {
        [System.Diagnostics.Conditional("DEBUG")]
        public static void WriteLine(string str)
        {
            //Console.WriteLine(str);
        }
    }

    enum ConnectionState
    {
        Disconnected,
        Connected
    }

    //enum ReadStepCode
    //{
    //    Connection,
    //    ReadColumn,
    //    ReadRow,
    //    ReadEOF,
    //    ReadErrOrOk,
    //}


    abstract class MySqlPacketParser
    {

        public abstract void Parse(byte[] buffer, int count);

        public abstract MySqlResult ResultPacket { get; }
    }

    class ResultPacketParser : MySqlPacketParser
    {

        enum ResultPacketState
        {
            ExpectedResultSetHeader,
            ResultSet_Content,

            Expect_FieldHeader,
            Field_Content,
            // Field_EofContent,

            Expect_RowHeader,
            Row_Content,
            //Row_EofContent,
            Should_End,
        }

        ResultPacketState parsingState;

        PacketParser _parser = new PacketParser(Encoding.UTF8);
        const byte ERROR_CODE = 255;
        const byte EOF_CODE = 254;
        const byte OK_CODE = 0;

        PacketHeader header;
        Packet currentPacket;
        TableHeader tableHeader;
        ConnectionConfig config;
        bool isProtocol41;
        bool needMoreBuffer = false;
        MySqlResult _finalResult;
        List<RowDataPacket> rows = new List<RowDataPacket>();

        const int PACKET_HEADER_LENGTH = 4;

        public ResultPacketParser(ConnectionConfig config, bool isProtocol41)
        {
            this.config = config;
            this.isProtocol41 = isProtocol41;
        }

        void Parse()
        {
            needMoreBuffer = false;
            _finalResult = null;

            switch (parsingState)
            {
                case ResultPacketState.ExpectedResultSetHeader:
                    {
                        if (!_parser.Ensure(PACKET_HEADER_LENGTH + 1)) //check if length is enough to parse 
                        {
                            needMoreBuffer = true;
                            return;
                        }
                        //-------------------------------- 
                        header = _parser.ParsePacketHeader();
                        byte packetType = _parser.PeekByte();
                        switch (packetType)
                        {
                            case ERROR_CODE:
                                {
                                    var errPacket = new ErrPacket();
                                    errPacket.Header = header;
                                    uint packetLen = errPacket.GetPacketLength();
                                    currentPacket = errPacket;
                                    errPacket.ParsePacket(_parser);
                                    //------------------------
                                    this._finalResult = new MySqlError(errPacket);

                                } break;
                            case EOF_CODE:
                            case OK_CODE:
                                {
                                    var okPacket = new OkPacket(this.isProtocol41);
                                    okPacket.Header = header;
                                    uint packetLen = okPacket.GetPacketLength();
                                    currentPacket = okPacket;
                                    okPacket.ParsePacket(_parser);
                                    this._finalResult = new MySqlOk(okPacket);
                                    this.parsingState = ResultPacketState.Should_End;
                                } break;
                            default:
                                {
                                    //resultset packet
                                    var resultPacket = new ResultSetHeaderPacket();
                                    currentPacket = resultPacket;
                                    resultPacket.Header = header;
                                    this.parsingState = ResultPacketState.ResultSet_Content;
                                } break;
                        }
                    } break;
                case ResultPacketState.ResultSet_Content:
                    {
                        if (!_parser.Ensure(header.ContentLength))
                        {
                            needMoreBuffer = true;
                            return;
                        }


                        //can parse
                        currentPacket.ParsePacket(_parser);

                        tableHeader = new TableHeader();
                        tableHeader.ConnConfig = this.config;
                        tableHeader.TypeCast = this.config.typeCast;
                        this.parsingState = ResultPacketState.Expect_FieldHeader;
                        rows = new List<RowDataPacket>();


                    } break;
                case ResultPacketState.Expect_FieldHeader:
                    {
                        if (!_parser.Ensure(PACKET_HEADER_LENGTH + 1)) //check if length is enough to parse 
                        {
                            needMoreBuffer = true;
                            return;
                        }

                        header = _parser.ParsePacketHeader();
                        byte packetType = _parser.PeekByte();
                        switch (packetType)
                        {
                            case ERROR_CODE:
                                {
                                } break;
                            case EOF_CODE:
                            case OK_CODE:
                                {
                                    //after field
                                    //expected field eof  
                                    var eofPacket = new EofPacket(this.isProtocol41);
                                    eofPacket.Header = header;
                                    uint packetLen = eofPacket.GetPacketLength();
                                    currentPacket = eofPacket;
                                    eofPacket.ParsePacket(_parser);

                                    this.parsingState = ResultPacketState.Expect_RowHeader;

                                } break;
                            default:
                                {
                                    FieldPacket fieldPacket = new FieldPacket(this.isProtocol41);
                                    fieldPacket.Header = header;
                                    tableHeader.AddField(fieldPacket);
                                    currentPacket = fieldPacket;
                                    this.parsingState = ResultPacketState.Field_Content;

                                } break;
                        }

                    } break;

                case ResultPacketState.Field_Content:
                    {
                        if (!_parser.Ensure(header.ContentLength)) //check if length is enough to parse 
                        {
                            needMoreBuffer = true;
                            return;
                        }

                        //can parse
                        currentPacket.ParsePacket(_parser);
                        this.parsingState = ResultPacketState.Expect_FieldHeader;
                    }
                    break;
                case ResultPacketState.Expect_RowHeader:
                    {
                        if (!_parser.Ensure(PACKET_HEADER_LENGTH + 1))
                        {
                            needMoreBuffer = true;
                            return;
                        }

                        header = _parser.ParsePacketHeader();
                        byte packetType = _parser.PeekByte();
                        switch (packetType)
                        {
                            case ERROR_CODE:
                                {
                                    //found error

                                } break;
                            case EOF_CODE://0x00 or 0xfe the OK packet header
                            case OK_CODE:
                                {
                                    //finish
                                    EofPacket eofPacket = new EofPacket(this.isProtocol41);
                                    eofPacket.Header = header;
                                    currentPacket = eofPacket;
                                    eofPacket.ParsePacket(_parser);

                                    this.parsingState = ResultPacketState.Should_End;
                                    //table result?
                                    _finalResult = new MySqlTableResult(tableHeader, rows);
                                    rows = null;

                                } break;
                            default:
                                {
                                    RowDataPacket rowPacket = new RowDataPacket(tableHeader);
                                    rowPacket.Header = header;
                                    currentPacket = rowPacket;
                                    rows.Add(rowPacket);
                                    this.parsingState = ResultPacketState.Row_Content;
                                } break;
                        }

                    } break;
                case ResultPacketState.Row_Content:
                    {
                        if (!_parser.Ensure(header.ContentLength))
                        {
                            needMoreBuffer = true;
                            return;
                        }

                        //can parse
                        currentPacket.ParsePacket(_parser);
                        this.parsingState = ResultPacketState.Expect_RowHeader;

                    } break;
                
            }
        }
        public override void Parse(byte[] buffer, int count)
        {
            _finalResult = null;
            _parser.AppendBuffer(buffer, count);
            for (; ; )
            {
                //loop
                Parse();
                if (needMoreBuffer)
                {
                    return;
                }
                else if (parsingState == ResultPacketState.Should_End)
                {
                    //reset
                    this._parser.Reset();
                    this.parsingState = ResultPacketState.ExpectedResultSetHeader;
                    return;
                }
            }
        }
        public override MySqlResult ResultPacket
        {
            get
            {
                return _finalResult;
            }
        }
    }



    class MySqlConnectionPacketParser : MySqlPacketParser
    {

        PacketParser _parser = new PacketParser(Encoding.UTF8);
        HandshakePacket _handshake;
        MySqlHandshakeResult _finalResult;

        public MySqlConnectionPacketParser()
        {
        }


        public override MySqlResult ResultPacket
        {
            get
            {
                return _finalResult;
            }
        }

        public override void Parse(byte[] buffer, int count)
        {
            _finalResult = null;
            //1.create connection frame  
            //_writer.Reset();  
            _parser.LoadNewBuffer(buffer, count);

            _handshake = new HandshakePacket();
            _handshake.ParsePacket(_parser);
            _finalResult = new MySqlHandshakeResult(_handshake);
        }

    }

    abstract class MySqlResult
    {
        public bool IsError { get; protected set; }
    }
    class MySqlHandshakeResult : MySqlResult
    {
        public readonly HandshakePacket packet;
        public MySqlHandshakeResult(HandshakePacket packet)
        {
            this.packet = packet;
        }
    }
    class MySqlError : MySqlResult
    {
        public readonly ErrPacket errPacket;
        public MySqlError(ErrPacket errPacket)
        {
            this.errPacket = errPacket;
            this.IsError = true;
        }
    }
    class MySqlOk : MySqlResult
    {
        public readonly OkPacket okpacket;
        public MySqlOk(OkPacket okpacket)
        {
            this.okpacket = okpacket;
        }
    }
    class MySqlTableResult : MySqlResult
    {
        public readonly TableHeader tableHeader;
        public readonly List<RowDataPacket> rows;
        public MySqlTableResult(TableHeader tableHeader, List<RowDataPacket> rows)
        {
            this.tableHeader = tableHeader;
            this.rows = rows;
        }
    }



    class MySqlParserMx : IDisposable
    {
        MemoryStream ms;
        RecvIO recvIO;
        int state = 0;
        MySqlPacketParser currentPacketParser; //current parser
        PacketWriter _writer;
        bool _isCompleted;
        public MySqlParserMx(RecvIO recvIO, PacketWriter _writer)
        {
            ms = new MemoryStream();
            this.recvIO = recvIO;
            this._writer = _writer;
        }
        public MySqlResult ResultPacket
        {
            get;
            private set;
        }
        public MySqlPacketParser CurrentPacketParser
        {
            get { return currentPacketParser; }
            set { currentPacketParser = value; }
        }
        public bool IsComplete
        {
            get { return _isCompleted; }
        }
        public void LoadData()
        {
            //we need to parse some data here 
            //load incomming data into ms 
            //load data from recv buffer into the ms  

            ResultPacket = null;
            //---------------
            //copy all to stream
            //---------------  
            byte[] buffer = new byte[512];
            int count = recvIO.BytesTransferred;
            if (count > 0)
            {
                if (count > 512)
                {
                    throw new NotSupportedException();
                }
                recvIO.ReadTo(0, buffer, count);
            }
            currentPacketParser.Parse(buffer, count);//may not complete in first round *** 
            ResultPacket = currentPacketParser.ResultPacket;
            _isCompleted = ResultPacket != null;
        }
        public void Dispose()
        {
            if (ms != null)
            {
                ms.Close();
                ms.Dispose();
                ms = null;
            }
        }


    }
    enum ProcessReceiveBufferResult
    {
        Error,
        Continue,
        Complete
    }

    partial class Connection
    {
        public ConnectionConfig config;
        public bool connectionCall;
        public ConnectionState State
        {
            get
            {
                return socket.Connected ? ConnectionState.Connected : ConnectionState.Disconnected;
            }
        }


        public uint threadId;
        Socket socket;


        Query _query;
        PacketParser _parser;
        PacketWriter _writer;
        //TODO: review how to clear remaining buffer again
        byte[] _tmpForClearRecvBuffer; //for clear buffer 
        /// <summary>
        /// max allowed packet size
        /// </summary>
        long _maxPacketSize = 0;

        //------------------------

        readonly SocketAsyncEventArgs recvSendArgs;
        readonly RecvIO recvIO;
        readonly SendIO sendIO;
        MySqlParserMx _mysqlParserMx;

        readonly int recvBufferSize = 2048; //set this a config
        readonly int sendBufferSize = 512;

        Action<MySqlResult> whenRecvComplete;
        Action<object> whenSendComplete;
        bool connectedIsComplete = false;
        MySqlPacketParser packetParser = null;
        internal MySqlParserMx SqlPacketParser { get { return _mysqlParserMx; } }
        bool isProtocol41;


        public Connection(ConnectionConfig userConfig)
        {
            config = userConfig;
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            //protocol = null;
            connectionCall = false;
            //this.config = options.config;
            //this._socket        = options.socket;
            //this._protocol      = new Protocol({config: this.config, connection: this});
            //this._connectCalled = false;
            //this.state          = "disconnected";
            //this.threadId       = null;
            switch ((CharSets)config.charsetNumber)
            {
                case CharSets.UTF8_GENERAL_CI:
                    _parser = new PacketParser(Encoding.UTF8);
                    _writer = new PacketWriter(Encoding.UTF8);
                    break;
                case CharSets.ASCII:
                    _parser = new PacketParser(Encoding.ASCII);
                    _writer = new PacketWriter(Encoding.ASCII);
                    break;
                default:
                    throw new NotImplementedException();
            }

            //------------------
            recvSendArgs = new SocketAsyncEventArgs();

            recvSendArgs.SetBuffer(new byte[recvBufferSize + sendBufferSize], 0, recvBufferSize + sendBufferSize);
            recvIO = new RecvIO(recvSendArgs, recvSendArgs.Offset, recvBufferSize, HandleReceive);
            sendIO = new SendIO(recvSendArgs, recvSendArgs.Offset + recvBufferSize, sendBufferSize, HandleSend);
            _mysqlParserMx = new MySqlParserMx(recvIO, _writer);

            //common(shared) event listener***
            recvSendArgs.Completed += (object sender, SocketAsyncEventArgs e) =>
            {
                switch (e.LastOperation)
                {
                    case SocketAsyncOperation.Receive:
                        recvIO.ProcessReceivedData();
                        break;
                    case SocketAsyncOperation.Send:
                        sendIO.ProcessWaitingData();
                        break;
                    default:
                        throw new ArgumentException("The last operation completed on the socket was not a receive or send");
                }
            };
            //------------------
            recvSendArgs.AcceptSocket = socket;
        }

        void UnBindSocket(bool keepAlive)
        {
            throw new NotImplementedException();
        }
        void HandleReceive(RecvEventCode recvEventCode)
        {
            switch (recvEventCode)
            {
                default: throw new NotSupportedException();

                case RecvEventCode.SocketError:
                    {
                        UnBindSocket(true);
                    }
                    break;
                case RecvEventCode.NoMoreReceiveData:
                    {

                    }
                    break;
                case RecvEventCode.HasSomeData:
                    {
                        //process some data
                        //there some data to process  
                        //parse the data  
                        _mysqlParserMx.LoadData();

                        if (_mysqlParserMx.ResultPacket != null)
                        {
                            if (whenRecvComplete != null)
                            {
                                whenRecvComplete(_mysqlParserMx.ResultPacket);
                            }
                            else
                            {
                                connectedIsComplete = true;
                            }
                        }
                        else
                        {
                            //no result packet
                        }
                        //switch (incommingStream.LoadData())
                        //{
                        //    case ProcessReceiveBufferResult.Complete:

                        //        if (whenRecvComplete != null)
                        //        {
                        //            whenRecvComplete(incommingStream.ResultPacket);
                        //        }
                        //        else
                        //        {
                        //            connectedIsComplete = true;
                        //        }
                        //        break;
                        //    case ProcessReceiveBufferResult.Continue:
                        //        break;
                        //    case ProcessReceiveBufferResult.Error:
                        //        break;
                        //}
                    }
                    break;
            }
        }
        void HandleSend(SendIOEventCode sendEventCode)
        {
            //throw new NotImplementedException();
            switch (sendEventCode)
            {
                case SendIOEventCode.SocketError:
                    {
                        UnBindSocket(true);
                    }
                    break;
                case SendIOEventCode.SendComplete:
                    {
                        if (whenSendComplete != null)
                        {
                            whenSendComplete(null);
                        }
                        //Reset();
                        //if (KeepAlive)
                        //{
                        //    StartReceive();
                        //}
                        //else
                        //{
                        //    UnBindSocket(true);
                        //}
                    }
                    break;
            }
        }
        internal void StartReceive()
        {
            whenRecvComplete = null;
            recvIO.StartReceive();
        }


        internal void StartReceive(Action<MySqlResult> whenCompleteAction)
        {
            this.whenRecvComplete = whenCompleteAction;
            recvIO.StartReceive();
        }


        public void Connect(Action onAsyncComplete = null)
        {
            if (State == ConnectionState.Connected)
            {
                throw new NotSupportedException("already connected");
            }

            var endpoint = new IPEndPoint(IPAddress.Parse(config.host), config.port);
            socket.Connect(endpoint); //start listen after connect***
            //1. 
            _mysqlParserMx.CurrentPacketParser = packetParser = new MySqlConnectionPacketParser();
            StartReceive(mysql_result =>
            {
                //when complete1
                //create handshake packet and send back
                var handShakeResult = mysql_result as MySqlHandshakeResult;
                if (handShakeResult == null)
                {
                    //error
                    throw new Exception("err1");
                }
                var handshake_packet = handShakeResult.packet;
                byte[] token = MakeToken(config.password,
                   GetScrollbleBuffer(handshake_packet.scrambleBuff1, handshake_packet.scrambleBuff2));
                _writer.IncrementPacketNumber();


                //----------------------------
                //send authen packet to the server
                var authPacket = new ClientAuthenticationPacket();
                authPacket.SetValues(config.user, token, config.database, isProtocol41 = handshake_packet.protocol41);
                authPacket.WritePacket(_writer);
                byte[] sendBuff = _writer.ToArray();
                SendData(sendBuff, 0, sendBuff.Length);

                _mysqlParserMx.CurrentPacketParser = packetParser = new ResultPacketParser(this.config, isProtocol41);

                StartReceive(mysql_result2 =>
                {
                    var ok = mysql_result2 as MySqlOk;
                    if (ok != null)
                    {
                        ConnectedSuccess = true;
                    }
                    else
                    {
                        //TODO: review here
                        //error 
                        ConnectedSuccess = false;
                    }

                    //ok
                    _writer.Reset();
                    //set max allow of the server ***
                    //todo set max allow packet***
                    connectedIsComplete = true;
                    if (onAsyncComplete != null)
                    {
                        onAsyncComplete();
                    }

                });
            });

            if (onAsyncComplete == null)
            {
                //exec as sync
                //so wait until complete
                //-------------------------------
                while (!connectedIsComplete) ;  //wait, or use thread sleep
                //-------------------------------
            }





            //create authen packet and send data back

            //byte[] buffer = new byte[512];
            //int count = socket.Receive(buffer);
            //if (count > 0)
            //{
            //    _writer.Reset();
            //    _parser.LoadNewBuffer(buffer, count);
            //    _handshake = new HandshakePacket();
            //    _handshake.ParsePacket(_parser);
            //    threadId = _handshake.threadId;
            //    byte[] token = MakeToken(config.password,
            //        GetScrollbleBuffer(_handshake.scrambleBuff1, _handshake.scrambleBuff2));
            //    _writer.IncrementPacketNumber();
            //    //------------------------------------------
            //    var authPacket = new ClientAuthenticationPacket();
            //    authPacket.SetValues(config.user, token, config.database, _handshake.protocol41);
            //    authPacket.WritePacket(_writer);
            //    byte[] sendBuff = _writer.ToArray();
            //    byte[] receiveBuff = new byte[512];
            //    //-------------------------------------------
            //    //send data
            //    int sendNum = socket.Send(sendBuff);
            //    int receiveNum = socket.Receive(receiveBuff);
            //    _parser.LoadNewBuffer(receiveBuff, receiveNum);
            //    if (receiveBuff[4] == 255)
            //    {
            //        ErrPacket errPacket = new ErrPacket();
            //        errPacket.ParsePacket(_parser);
            //        return;
            //    }
            //    else
            //    {
            //        OkPacket okPacket = new OkPacket(_handshake.protocol41);
            //        okPacket.ParsePacket(_parser);
            //    }
            //    _writer.Reset();
            //    GetMaxAllowedPacket();
            //    _writer.SetMaxAllowedPacket(_maxPacketSize);
            //}
        }

        public bool ConnectedSuccess
        {
            get;
            private set;
        }

        public void GetMaxAllowedPacket()
        {
            _query = CreateQuery("SELECT @@global.max_allowed_packet", null);
            _query.Execute();

            if (_query.LoadError != null)
            {
                dbugConsole.WriteLine("Error Message : " + _query.LoadError.message);
            }
            else if (_query.OkPacket != null)
            {
                dbugConsole.WriteLine("OkPacket : " + _query.OkPacket.affectedRows);
            }
            else
            {
                if (_query.ReadRow())
                {
                    _maxPacketSize = _query.Cells[0].myInt64;
                }
            }
        }

        public Query CreateQuery(string sql, CommandParams command)//testing
        {
            return new Query(this, sql, command);
        }


        void CreateNewSocket()
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            Connect();
        }

        public void Disconnect()
        {
            _writer.Reset();
            ComQuitPacket quitPacket = new ComQuitPacket();
            quitPacket.WritePacket(_writer);
            int send = socket.Send(_writer.ToArray());
            socket.Disconnect(true);
        }
        public bool IsStoredInConnPool { get; set; }
        public bool IsInUsed { get; set; }

        internal PacketParser PacketParser
        {
            get { return _parser; }
        }
        internal PacketWriter PacketWriter
        {
            get { return _writer; }
        }
        internal bool IsProtocol41 { get { return this.isProtocol41; } }

        internal void ClearRemainingInputBuffer()
        {
            //TODO: review here again

            int lastReceive = 0;
            long allReceive = 0;
            int i = 0;
            if (socket.Available > 0)
            {
                if (_tmpForClearRecvBuffer == null)
                {
                    _tmpForClearRecvBuffer = new byte[300000];//in test case socket recieve lower than 300,000 bytes
                }

                while (socket.Available > 0)
                {
                    lastReceive = socket.Receive(_tmpForClearRecvBuffer);
                    allReceive += lastReceive;
                    i++;
                    //TODO: review here again
                    dbugConsole.WriteLine("i : " + i + ", lastReceive : " + lastReceive);
                    Thread.Sleep(100);
                }
                dbugConsole.WriteLine("All Receive bytes : " + allReceive);
            }
        }
        public int SendDataAsync(byte[] sendBuffer, int start, int len, Action<object> whenSendComplete)
        {
            this.whenSendComplete = whenSendComplete;
            sendIO.EnqueueOutputData(sendBuffer, len);
            sendIO.StartSendAsync();
            return 0;
        }
        public int SendData(byte[] sendBuffer, int start, int len)
        {
            return socket.Send(sendBuffer, start, len, SocketFlags.None);
        }
        public int ReceiveData(byte[] recvBuffer)
        {
            return socket.Receive(recvBuffer);
        }
        public int ReceiveData(byte[] recvBuffer, int writePos, int reqLength)
        {
            return socket.Receive(recvBuffer, writePos, reqLength, SocketFlags.None);
        }
        public int Available
        {
            get
            {
                return socket.Available;
            }
        }
        static byte[] GetScrollbleBuffer(byte[] part1, byte[] part2)
        {
            return ConcatBuffer(part1, part2);
        }

        static byte[] MakeToken(string password, byte[] scramble)
        {
            // password must be in binary format, not utf8
            //var stage1 = sha1((new Buffer(password, "utf8")).toString("binary"));
            //var stage2 = sha1(stage1);
            //var stage3 = sha1(scramble.toString('binary') + stage2);
            //return xor(stage3, stage1);
            var buff1 = Encoding.UTF8.GetBytes(password.ToCharArray());
            var sha = new System.Security.Cryptography.SHA1Managed();
            // This is one implementation of the abstract class SHA1.
            //scramble = new byte[] { 52, 78, 110, 96, 117, 75, 85, 75, 87, 83, 121, 44, 106, 82, 62, 123, 113, 73, 84, 77 };
            byte[] stage1 = sha.ComputeHash(buff1);
            byte[] stage2 = sha.ComputeHash(stage1);
            //merge scramble and stage2 again
            byte[] combineFor3 = ConcatBuffer(scramble, stage2);
            byte[] stage3 = sha.ComputeHash(combineFor3);
            return xor(stage3, stage1);
        }

        static byte[] ConcatBuffer(byte[] a, byte[] b)
        {
            byte[] combine = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, combine, 0, a.Length);
            Buffer.BlockCopy(b, 0, combine, a.Length, b.Length);
            return combine;
        }

        static byte[] xor(byte[] a, byte[] b)
        {
            int j = a.Length;
            var result = new byte[j];
            for (int i = 0; i < j; ++i)
            {
                result[i] = (byte)(a[i] ^ b[i]);
            }
            return result;
        }
    }

    class ConnectionConfig
    {
        public string host;
        public int port;
        public string localAddress;//unknowed type
        public string socketPath;//unknowed type
        public string user;
        public string password;
        public string database;
        public int connectionTimeout;
        public bool insecureAuth;
        public bool supportBigNumbers;
        public bool bigNumberStrings;
        public bool dateStrings;
        public bool debug;
        public bool trace;
        public bool stringifyObjects;
        public string timezone;
        public string flags;
        public string queryFormat;
        public string pool;//unknowed type
        public string ssl;//string or bool
        public bool multipleStatements;
        public bool typeCast;
        public long maxPacketSize;
        public int charsetNumber;
        public int defaultFlags;
        public int clientFlags;
        public ConnectionConfig()
        {
            SetDefault();
        }

        public ConnectionConfig(string username, string password)
        {
            SetDefault();
            this.user = username;
            this.password = password;
        }
        public ConnectionConfig(string host, string username, string password, string database)
        {
            SetDefault();
            this.user = username;
            this.password = password;
            this.host = host;
            this.database = database;
        }
        void SetDefault()
        {
            //if (typeof options === 'string') {
            //  options = ConnectionConfig.parseUrl(options);
            //}
            host = "127.0.0.1";//this.host = options.host || 'localhost';
            port = 3306;//this.port = options.port || 3306;
            //this.localAddress       = options.localAddress;
            //this.socketPath         = options.socketPath;
            //this.user               = options.user || undefined;
            //this.password           = options.password || undefined;
            //this.database           = options.database;
            database = "";
            connectionTimeout = 10 * 1000;
            //this.connectTimeout     = (options.connectTimeout === undefined)
            //  ? (10 * 1000)
            //  : options.connectTimeout;
            insecureAuth = false;//this.insecureAuth = options.insecureAuth || false;
            supportBigNumbers = false;//this.supportBigNumbers = options.supportBigNumbers || false;
            bigNumberStrings = false;//this.bigNumberStrings = options.bigNumberStrings || false;
            dateStrings = false;//this.dateStrings = options.dateStrings || false;
            debug = false;//this.debug = options.debug || true;
            trace = false;//this.trace = options.trace !== false;
            stringifyObjects = false;//this.stringifyObjects = options.stringifyObjects || false;
            timezone = "local";//this.timezone = options.timezone || 'local';
            flags = "";//this.flags = options.flags || '';
            //this.queryFormat        = options.queryFormat;
            //this.pool               = options.pool || undefined;

            //this.ssl                = (typeof options.ssl === 'string')
            //  ? ConnectionConfig.getSSLProfile(options.ssl)
            //  : (options.ssl || false);
            multipleStatements = false;//this.multipleStatements = options.multipleStatements || false; 
            typeCast = true;
            //this.typeCast = (options.typeCast === undefined)
            //  ? true
            //  : options.typeCast;

            //if (this.timezone[0] == " ") {
            //  // "+" is a url encoded char for space so it
            //  // gets translated to space when giving a
            //  // connection string..
            //  this.timezone = "+" + this.timezone.substr(1);
            //}

            //if (this.ssl) {
            //  // Default rejectUnauthorized to true
            //  this.ssl.rejectUnauthorized = this.ssl.rejectUnauthorized !== false;
            //}

            maxPacketSize = 0;//this.maxPacketSize = 0;
            charsetNumber = (int)CharSets.UTF8_GENERAL_CI;
            //this.charsetNumber = (options.charset)
            //  ? ConnectionConfig.getCharsetNumber(options.charset)
            //  : options.charsetNumber||Charsets.UTF8_GENERAL_CI;

            //// Set the client flags
            //var defaultFlags = ConnectionConfig.getDefaultFlags(options);
            //this.clientFlags = ConnectionConfig.mergeFlags(defaultFlags, options.flags)
        }

        public void SetConfig(string host, int port, string username, string password, string database)
        {
            this.host = host;
            this.port = port;
            this.user = username;
            this.password = password;
            this.database = database;
        }
    }
}