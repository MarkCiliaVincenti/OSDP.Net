using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OSDP.Net.Connections;
using OSDP.Net.Logging;
using OSDP.Net.Messages;

namespace OSDP.Net
{
    /// <summary>
    /// A group of OSDP devices sharing communications
    /// </summary>
    internal class Bus
    {
        private const byte DriverByte = 0xFF;

        private static readonly ILog Logger = LogProvider.GetCurrentClassLogger();

        private readonly SortedSet<Device> _configuredDevices = new SortedSet<Device>();
        private readonly object _configuredDevicesLock = new object();
        private readonly IOsdpConnection _connection;

        private readonly TimeSpan _readTimeout = TimeSpan.FromMilliseconds(200);
        private readonly BlockingCollection<Reply> _replies;

        private bool _isShuttingDown;

        public Bus(IOsdpConnection connection, BlockingCollection<Reply> replies)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _replies = replies ?? throw new ArgumentNullException(nameof(replies));
            
            Id = Guid.NewGuid();
        }

        private TimeSpan IdleLineDelay => TimeSpan.FromSeconds(1.0/_connection.BaudRate * 16.0);

        /// <summary>
        /// Unique identifier of the bus
        /// </summary>
        public Guid Id { get; }

        /// <summary>
        /// Closes down the connection
        /// </summary>
        public void Close()
        {
            _isShuttingDown = true;
            _connection.Close();
        }

        /// <summary>
        /// Send a command to a device
        /// </summary>
        /// <param name="command">Details about the command</param>
        public void SendCommand(Command command)
        {
            var foundDevice = _configuredDevices.First(device => device.Address == command.Address);
            
            foundDevice.SendCommand(command);
        }

        /// <summary>
        /// Add a device to the bus
        /// </summary>
        /// <param name="address">Address of the device</param>
        /// <param name="useSecureChannel">Use a secure channel to communicate</param>
        public void AddDevice(byte address, bool useSecureChannel)
        {
            var foundDevice = _configuredDevices.FirstOrDefault(device => device.Address == address);

            lock (_configuredDevicesLock)
            {
                if (foundDevice != null)
                {
                    _configuredDevices.Remove(foundDevice);
                }

                _configuredDevices.Add(new Device(address, true, useSecureChannel));
            }
        }

        /// <summary>
        /// Remove a device from the bus
        /// </summary>
        /// <param name="address">Address of the device</param>
        public void RemoveDevice(byte address)
        {
            var foundDevice = _configuredDevices.FirstOrDefault(device => device.Address == address);
            if (foundDevice != null)
            {
                lock (_configuredDevicesLock)
                {
                    _configuredDevices.Remove(foundDevice);
                }
            }
        }

        /// <summary>
        /// Is the device currently online
        /// </summary>
        /// <param name="address">Address of the device</param>
        /// <returns>True if the device is online</returns>
        public bool IsOnline(byte address)
        {
            var foundDevice = _configuredDevices.First(device => device.Address == address);
            
            return  foundDevice.IsOnline;
        }

        /// <summary>
        /// Start polling the devices on the bus
        /// </summary>
        /// <returns></returns>
        public async Task StartPollingAsync()
        {
            DateTime lastMessageSentTime = DateTime.MinValue;

            while (!_isShuttingDown)
            {
                if (!_connection.IsOpen)
                {
                    _connection.Open();
                }

                TimeSpan timeDifference = TimeSpan.FromMilliseconds(100) - (DateTime.UtcNow - lastMessageSentTime);
                await Task.Delay(timeDifference > TimeSpan.Zero ? timeDifference : TimeSpan.Zero);

                foreach (var device in _configuredDevices.ToArray())
                {
                    var data = new List<byte> {DriverByte};
                    var command = device.GetNextCommandData();

                    byte[] commandData;
                    try
                    {
                        commandData = command.BuildCommand(device);
                    }
                    catch (Exception exception)
                    {
                        Logger.Error($"Error while building command {command}", exception);
                        continue;
                    }

                    data.AddRange(commandData);

                    Logger.Debug($"Raw write data: {BitConverter.ToString(commandData)}", Id, command.Address);

                    lastMessageSentTime = DateTime.UtcNow;

                    await _connection.WriteAsync(data.ToArray());

                    var replyBuffer = new Collection<byte>();

                    if (!await WaitForStartOfMessage(replyBuffer)) continue;

                    if (!await WaitForMessageLength(replyBuffer)) continue;

                    if (!await WaitForRestOfMessage(replyBuffer, ExtractMessageLength(replyBuffer))) continue;

                    var reply = Reply.Parse(replyBuffer, Id, command, device);

                    if (!reply.IsValidReply(device.MessageControl.Sequence)) continue;

                    if (reply.IsSecureMessage)
                    {
                        var mac = device.GenerateMac(reply.MessageForMacGeneration, false);
                        if (!reply.IsValidMac(mac))
                        {
                            device.ResetSecurity();
                            continue;
                        }
                    }

                    if (reply.Type != ReplyType.Busy || reply.Type != ReplyType.Nak)
                    {
                        device.ValidReplyHasBeenReceived();
                    }

                    if (reply.Type == ReplyType.Nak && reply.ExtractReplyData.First() == 0x06)
                    {
                        device.ResetSecurity();
                    }

                    switch (reply.Type)
                    {
                        case ReplyType.CrypticData:
                            device.SendCommand(new ServerCryptogramCommand(device.Address,
                                device.InitializeSecureChannel(reply).ToArray()));
                            break;
                        case ReplyType.InitialRMac:
                            device.ValidateSecureChannelEstablishment(reply);
                            break;
                    }
                    _replies.Add(reply);

                    Logger.Debug($"Raw reply data: {BitConverter.ToString(replyBuffer.ToArray())}", Id,
                        command.Address);

                    await Task.Delay(IdleLineDelay);
                }
            }
        }

        private static ushort ExtractMessageLength(IReadOnlyList<byte> replyBuffer)
        {
            return Message.ConvertBytesToShort(new[] {replyBuffer[2], replyBuffer[3]});
        }

        private async Task<bool> WaitForRestOfMessage(ICollection<byte> replyBuffer, ushort replyLength)
        {
            while (replyBuffer.Count < replyLength)
            {
                byte[] readBuffer = new byte[byte.MaxValue];
                int bytesRead = await TimeOutReadAsync(readBuffer);
                if (bytesRead > 0)
                {
                    for (byte index = 0; index < bytesRead; index++)
                    {
                        replyBuffer.Add(readBuffer[index]);
                    }
                }
                else
                {
                    return false;
                }
            }

            return true;
        }

        private async Task<bool> WaitForMessageLength(ICollection<byte> replyBuffer)
        {
            while (replyBuffer.Count < 4)
            {
                byte[] readBuffer = new byte[4];
                int bytesRead = await TimeOutReadAsync(readBuffer);
                if (bytesRead > 0)
                {
                    for (byte index = 0; index < bytesRead; index++)
                    {
                        replyBuffer.Add(readBuffer[index]);
                    }
                }
                else
                {
                    return false;
                }
            }

            return true;
        }

        private async Task<bool> WaitForStartOfMessage(ICollection<byte> replyBuffer)
        {
            while (true)
            {
                byte[] readBuffer = new byte[1];
                int bytesRead = await TimeOutReadAsync(readBuffer);
                if (bytesRead == 0)
                {
                    return false;
                }

                if (readBuffer[0] != Message.StartOfMessage)
                {
                    continue;
                }

                replyBuffer.Add(readBuffer[0]);
                break;
            }

            return true;
        }

        private async Task<int> TimeOutReadAsync(byte[] buffer)
        {
            using (var cancellationTokenSource = new CancellationTokenSource(_readTimeout))
            {
                try
                {
                    return await _connection.ReadAsync(buffer, cancellationTokenSource.Token);
                }
                catch (TaskCanceledException)
                {
                    return 0;
                }
            }
        }
    }
}