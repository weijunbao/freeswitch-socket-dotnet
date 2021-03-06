﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Net.Sockets;
using Org.Reddragonit.FreeSwitchSockets.Messages;
using System.Threading;
using System.Net;
using Org.Reddragonit.FreeSwitchSockets.Outbound;
using System.Xml;
using System.IO;

namespace Org.Reddragonit.FreeSwitchSockets
{
    public abstract class ASocket
    {
        private struct sEventHandler{
            private string _eventName;
            public string EventName{
                get{return _eventName;}
            }

            private string _uuid;
            public string UUID{
                get{return _uuid;}
            }

            private string _callerUUID;
            public string CallerUUID
            {
                get { return _callerUUID; }
            }

            private string _channelName;
            public string ChannelName
            {
                get { return _channelName; }
            }

            private delProcessEventMessage _handler;
            public delProcessEventMessage Handler
            {
                get { return _handler; }
            }

            private long _id;
            public long ID
            {
                get { return _id; }
            }

            public sEventHandler(string eventName, string uuid,string callerUUID,string channelName, delProcessEventMessage handler,long id)
            {
                _eventName = eventName;
                _uuid = uuid;
                _callerUUID = callerUUID;
                _channelName = channelName;
                _handler = handler;
                _id = id;
            }

            public bool HandlesEvent(SocketEvent Event){
                return ASocket.StringsEqual((EventName == null ? Event.EventName : EventName), Event.EventName) &&
                    ASocket.StringsEqual((UUID == null ? Event.UniqueID : UUID), Event.UniqueID) &&
                    ASocket.StringsEqual((ChannelName == null ? Event.ChannelName : ChannelName), Event.ChannelName) &&
                    ASocket.StringsEqual((CallerUUID == null ? Event.CallerUUID : CallerUUID), Event.CallerUUID);
            }
        }

        internal static bool StringsEqual(string str1, string str2)
        {
            if ((str1 == null) && (str2 != null))
                return false;
            else if ((str1 != null) && (str2 == null))
                return false;
            else if ((str1 == null) && (str2 == null))
                return true;
            else
                return str1.Equals(str2);
        }

        private const string MESSAGE_END_STRING = "\n\n";
        private const string REGISTER_EVENT_COMMAND = "event {0}";
        private const string REMOVE_EVENT_COMMAND = "nixevent {0}";
        private const string EVENT_FILTER_COMMAND = "filter {0} {1}";
        private const string REMOVE_EVENT_FILTER_COMMAND = "filter delete {0} {1}";
        private const string BACKGROUND_API_RESPONSE_EVENT = "SWITCH_EVENT_BACKGROUND_JOB";
        private const string API_ISSUE_COMMAND = "bgapi {0}";

        public delegate void delProcessEventMessage(SocketEvent message);
        public delegate void delDisposeInvalidMessage(string message);

        private Socket _socket;
        protected Socket socket
        {
            get { return _socket; }
        }

        private MT19937 _random = new MT19937(DateTime.Now.Ticks);
        protected MT19937 Random
        {
            get { return _random; }
        }

        private bool _isConnected = false;
        protected bool IsConnected
        {
            get { return _isConnected; }
            set { _isConnected = value; }
        }

        private FreeSwitchLogLevels _currentLevel = FreeSwitchLogLevels.CONSOLE;
        public FreeSwitchLogLevels LogLevel
        {
            get { return _currentLevel; }
            set
            {
                if ((int)value > (int)_currentLevel)
                {
                    _sendCommand("log " + value.ToString());
                    _currentLevel = value;
                }
            }
        }

        private string _textReceived;
        private List<string> _splitMessages;
        private List<string> _processingMessages;
        private List<sEventHandler> _handlers;
        protected Queue<byte[]> _awaitingCommands;
        private bool _exit = false;
        private IPAddress _ipAddress;
        private int _port;
        private string _currentCommandID;
        private delProcessEventMessage _eventProcessor;
        private Queue<ManualResetEvent> _awaitingCommandsEvents;
        private Dictionary<string, ManualResetEvent> _commandThreads;
        private Dictionary<string, string> _awaitingCommandReturns;
        private Thread _backgroundProcessor;
        private Thread _backgroundDataReader;
        private ManualResetEvent _mreMessageWaiting;
        private delDisposeInvalidMessage _disposeInvalidMesssage;
        public delDisposeInvalidMessage DisposeInvalidMessage
        {
            get { return _disposeInvalidMesssage; }
            set { _disposeInvalidMesssage = value; }
        }

        protected ASocket(Socket socket)
        {
            _textReceived = "";
            _processingMessages = new List<string>();
            _splitMessages = new List<string>();
            _awaitingCommandsEvents = new Queue<ManualResetEvent>();
            _awaitingCommandReturns = new Dictionary<string, string>();
            _commandThreads = new Dictionary<string, ManualResetEvent>();
            _awaitingCommandReturns = new Dictionary<string, string>();
            _eventProcessor = new delProcessEventMessage(ProcessEvent);
            _handlers = new List<sEventHandler>();
            _socket = socket;
            _isConnected = _socket.Connected;
            if (!_isConnected)
                throw new Exception("Unable to construct an instance of the abstract class ASocket using the contructor with a socket without passing a connected socket.");
            _ipAddress = ((IPEndPoint)_socket.RemoteEndPoint).Address;
            _port = ((IPEndPoint)_socket.RemoteEndPoint).Port;
            _preSocketReady();
            _mreMessageWaiting = new ManualResetEvent(false);
            _backgroundProcessor = new Thread(new ThreadStart(_MessageProcessorStart));
            _backgroundProcessor.IsBackground = true;
            _backgroundProcessor.Start();
            _socket.ReceiveTimeout = 1000;
            _backgroundDataReader = new Thread(new ThreadStart(_SocketDataReaderStart));
            _backgroundDataReader.IsBackground = true;
            _backgroundDataReader.Start();
            this.RegisterEvent(BACKGROUND_API_RESPONSE_EVENT);
        }

        protected string _IssueAPICommand(string command, bool api)
        {
            ManualResetEvent mre = new ManualResetEvent(false);
            lock (_awaitingCommandsEvents)
            {
                _awaitingCommandsEvents.Enqueue(mre);
            }
            string comID="";
            _sendCommand(string.Format(API_ISSUE_COMMAND, command));
            mre.WaitOne();
            if (api)
            {
                lock (_commandThreads)
                {
                    mre = new ManualResetEvent(false);
                    _commandThreads.Add(_currentCommandID, mre);
                    comID = _currentCommandID;
                }
                string ret = "";
                mre.WaitOne();
                lock (_awaitingCommandReturns)
                {
                    ret = _awaitingCommandReturns[comID];
                    _awaitingCommandReturns.Remove(comID);
                }
                return ret.Trim('\n');
            }
            return "";
        }

        protected ASocket(IPAddress ip, int port)
        {
            _textReceived = "";
            _processingMessages = new List<string>();
            _splitMessages = new List<string>();
            _awaitingCommandsEvents = new Queue<ManualResetEvent>();
            _awaitingCommandReturns = new Dictionary<string, string>();
            _commandThreads = new Dictionary<string, ManualResetEvent>();
            _awaitingCommandReturns = new Dictionary<string, string>();
            _eventProcessor = new delProcessEventMessage(ProcessEvent);
            _handlers = new List<sEventHandler>();
            _exit = false;
            _isConnected = false;
            _ipAddress = ip;
            _port = port;
            _awaitingCommands = new Queue<byte[]>();
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _mreMessageWaiting = new ManualResetEvent(false);
            _backgroundProcessor = new Thread(new ThreadStart(_MessageProcessorStart));
            _backgroundProcessor.Start();
            _socket.ReceiveTimeout = 1000;
            _preSocketReady();
            Thread th = new Thread(new ThreadStart(BackgroundRun));
            th.IsBackground = true;
            th.Start();
        }

        private void BackgroundRun()
        {
            while (!_exit)
            {
                if (_isConnected == false)
                {
                    try
                    {
                        _socket.Connect(_ipAddress, _port);
                        _backgroundDataReader = new Thread(new ThreadStart(_SocketDataReaderStart));
                        _backgroundDataReader.Start();
                    }
                    catch (Exception e)
                    {
                    }
                    if (!_isConnected)
                        Thread.Sleep(100);
                    else
                        break;
                }
            }
        }

        protected void _sendCommand(string commandString)
        {
            if (!commandString.EndsWith(MESSAGE_END_STRING))
                commandString += MESSAGE_END_STRING;
            _sendCommand(System.Text.ASCIIEncoding.ASCII.GetBytes(commandString));
        }

        protected void _sendCommand(byte[] commandBytes)
        {
            if (!_isConnected)
            {
                lock (_awaitingCommands)
                {
                    if (_isConnected)
                        _socket.Send(commandBytes, 0, commandBytes.Length, SocketFlags.None);
                    else
                        _awaitingCommands.Enqueue(commandBytes);
                }
            }
            else
            {
                try
                {
                    _socket.Send(commandBytes, 0, commandBytes.Length, SocketFlags.None);
                }
                catch (Exception e)
                {
                    if (e is ObjectDisposedException)
                    {
                        _exit = true;
                        _isConnected = false;
                    }
                    throw e;
                }
            }
        }

        public void Close()
        {
            _exit = true;
            _close();
            _sendCommand("exit");
            try
            {
                _socket.Disconnect(false);
                _socket.Close();
            }
            catch (Exception e)
            {
            }
        }

        public long RegisterEventHandler(string eventName, string uuid, string callerUUID, string channelName, delProcessEventMessage handler)
        {
            long id = _random.NextLong();
            lock (_handlers)
            {
                _handlers.Add(new sEventHandler(eventName, uuid, callerUUID, channelName, handler, id));
            }
            return id;
        }

        public void UnRegisterEventHandler(long id)
        {
            lock (_handlers)
            {
                for (int x = 0; x < _handlers.Count; x++)
                {
                    if (_handlers[x].ID == id)
                    {
                        _handlers.RemoveAt(x);
                        break;
                    }
                }
            }
        }

        public void RegisterEvent(string eventName)
        {
            _sendCommand(string.Format(REGISTER_EVENT_COMMAND, eventName));
        }

        public void UnRegister(string eventName)
        {
            _sendCommand(string.Format(REMOVE_EVENT_COMMAND, eventName));
        }

        public void RegisterEventFilter(string fieldName, string fieldValue)
        {
            _sendCommand(string.Format(EVENT_FILTER_COMMAND, fieldName, fieldValue));
        }

        public void UnRegisterEventFilter(string fieldName, string fieldValue)
        {
            _sendCommand(string.Format(REMOVE_EVENT_FILTER_COMMAND, fieldName, fieldValue));
        }

        protected abstract void _processMessageQueue(Queue<ASocketMessage> messages);
        protected abstract void _close();
        protected abstract void _preSocketReady();

        private void _SocketDataReaderStart()
        {
            Thread.CurrentThread.IsBackground = true;
            Thread.CurrentThread.Name = "FreeSwitchSocketDataReader_" + Thread.CurrentThread.ManagedThreadId.ToString();
            byte[] buffer;
            while (!_exit)
            {
                buffer =new byte[500];
                int bytesRead = 0;
                try
                {
                    if (_socket.Available > 0)
                        bytesRead = _socket.Receive(buffer);
                    else
                        bytesRead = 0;
                }
                catch (Exception e)
                {
                    bytesRead = 0;
                }
                if (bytesRead > 0)
                {
                    lock (_textReceived)
                    {
                        _textReceived += ASCIIEncoding.ASCII.GetString(buffer, 0, bytesRead);
                        _textReceived = _textReceived.TrimStart('\n');
                        bool trigger = false;
                        lock (_splitMessages)
                        {
                            while (_textReceived.Contains(MESSAGE_END_STRING))
                            {
                                trigger = true;
                                _splitMessages.Add(_textReceived.Substring(0, _textReceived.IndexOf(MESSAGE_END_STRING)).Trim('\n'));
                                _textReceived = _textReceived.Substring(_textReceived.IndexOf(MESSAGE_END_STRING) + MESSAGE_END_STRING.Length);
                            }
                            if (trigger)
                                _mreMessageWaiting.Set();
                        }
                    }
                }
                else
                    Thread.Sleep(100);
            }
        }

        private void _MessageProcessorStart()
        {
            Thread.CurrentThread.Name = "EventMessageProcessor_" + Thread.CurrentThread.ManagedThreadId.ToString();
            while (!_exit)
            {
                if (_mreMessageWaiting.WaitOne(1000))
                {
                    lock (_splitMessages)
                    {
                        while (_splitMessages.Count > 0)
                        {
                            _processingMessages.Add(_splitMessages[0]);
                            _splitMessages.RemoveAt(0);
                        }
                    }
                    bool run = true;
                    Queue<ASocketMessage> msgs = new Queue<ASocketMessage>();
                    while (run)
                    {
                        while (_processingMessages.Count > 0)
                        {
                            string origMsg = _processingMessages[0];
                            _processingMessages.RemoveAt(0);
                            Dictionary<string, string> pars = ASocketMessage.ParseProperties(origMsg);
                            string subMsg = "";
                            //fail safe for delayed header
                            if (!pars.ContainsKey("Content-Type"))
                            {
                                if (_disposeInvalidMesssage != null)
                                    _disposeInvalidMesssage(origMsg);
                                break;
                            }
                            if (pars.ContainsKey("Content-Length"))
                            {
                                if (int.Parse(pars["Content-Length"]) > 0)
                                {
                                    if (_processingMessages.Count > 0)
                                    {
                                        subMsg = _processingMessages[0];
                                        _processingMessages.RemoveAt(0);
                                    }
                                    else
                                    {
                                        _processingMessages.Insert(0, origMsg);
                                        break;
                                    }
                                }
                            }
                            switch (pars["Content-Type"])
                            {
                                case "text/event-plain":
                                    if (subMsg == "")
                                    {
                                        _processingMessages.Insert(0, origMsg);
                                        break;
                                    }
                                    else
                                    {
                                        SocketEvent se;
                                        se = new SocketEvent(subMsg);
                                        if (se["Content-Length"] != null)
                                        {
                                            if (_processingMessages.Count > 0)
                                            {
                                                se.Message = _processingMessages[0];
                                                _processingMessages.RemoveAt(0);
                                            }
                                            else
                                            {
                                                _processingMessages.Insert(0, origMsg);
                                                _processingMessages.Insert(1, subMsg);
                                                break;
                                            }
                                        }
                                        if (se.EventName == "BACKGROUND_JOB")
                                        {
                                            lock (_commandThreads)
                                            {
                                                if (_commandThreads.ContainsKey(se["Job-UUID"]))
                                                {
                                                    lock (_awaitingCommandReturns)
                                                    {
                                                        _awaitingCommandReturns.Add(se["Job-UUID"], se.Message.Trim('\n'));
                                                    }
                                                    ManualResetEvent mre = _commandThreads[se["Job-UUID"]];
                                                    _commandThreads.Remove(se["Job-UUID"]);
                                                    mre.Set();
                                                }
                                            }
                                        }
                                        msgs.Enqueue(se);
                                    }
                                    break;
                                case "command/reply":
                                    CommandReplyMessage crm = new CommandReplyMessage(origMsg, subMsg);
                                    msgs.Enqueue(crm);
                                    if (crm["Job-UUID"] != null)
                                    {
                                        lock (_awaitingCommandsEvents)
                                        {
                                            _currentCommandID = crm["Job-UUID"];
                                            _awaitingCommandsEvents.Dequeue().Set();
                                        }
                                    }
                                    break;
                                case "log/data":
                                    SocketLogMessage lg;
                                    lg = new SocketLogMessage(subMsg);
                                    if (_processingMessages.Count > 0)
                                    {
                                        string eventMsg = _processingMessages[0];
                                        _processingMessages.RemoveAt(0);
                                        lg.FullMessage = eventMsg;
                                        msgs.Enqueue(lg);
                                    }
                                    else
                                    {
                                        _processingMessages.Insert(0, origMsg);
                                        _processingMessages.Insert(1, subMsg);
                                        break;
                                    }
                                    break;
                                case "text/disconnect-notice":
                                    msgs.Enqueue(new DisconnectNoticeMessage(origMsg));
                                    break;
                                case "auth/request":
                                    msgs.Enqueue(new AuthenticationRequestMessage(origMsg));
                                    break;
                                default:
                                    if (_disposeInvalidMesssage != null)
                                        _disposeInvalidMesssage(origMsg);
                                    break;
                            }
                        }
                        if (msgs.Count > 0)
                            _processMessageQueue(msgs);
                        lock (_processingMessages)
                        {
                            lock (_splitMessages)
                            {
                                if (_splitMessages.Count > 0)
                                {
                                    while (_splitMessages.Count > 0)
                                    {
                                        _processingMessages.Add(_splitMessages[0]);
                                        _splitMessages.RemoveAt(0);
                                    }
                                }
                                else
                                {
                                    run = false;
                                }
                            }
                        }
                    }
                }
                lock (_splitMessages)
                {
                    if (_splitMessages.Count == 0)
                        _mreMessageWaiting.Reset();
                }
            }
        }

        private void ProcessEvent(SocketEvent message)
        {
            sEventHandler[] handlers;
            lock (_handlers)
            {
                handlers = new sEventHandler[_handlers.Count];
                _handlers.CopyTo(handlers, 0);
            }
            foreach (sEventHandler eh in handlers)
            {
                if (eh.HandlesEvent(message))
                    eh.Handler.BeginInvoke(message, new AsyncCallback(ProcessingComplete), this);
            }
        }

        private void ProcessingComplete(IAsyncResult res)
        {
        }
    }
}
