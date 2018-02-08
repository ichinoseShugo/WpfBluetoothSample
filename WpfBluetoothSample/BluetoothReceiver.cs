using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace WpfBluetoothSample
{
    class BluetoothReceiver
    {
        /*
        private DeviceWatcher deviceWatcher = null;
        private StreamSocket chatSocket = null;
        private DataWriter chatWriter = null;
        private RfcommDeviceService chatService = null;
        private BluetoothDevice bluetoothDevice;

        // チャットする利用可能なデバイスのリストを表示するために使用されます
        public ObservableCollection<RfcommChatDeviceDisplay> ResultCollection
        {
            get;
            private set;
        }

        private void StopWatcher()
        {
            if (null != deviceWatcher)
            {
                if ((DeviceWatcherStatus.Started == deviceWatcher.Status ||
                     DeviceWatcherStatus.EnumerationCompleted == deviceWatcher.Status))
                {
                    deviceWatcher.Stop();
                }
                deviceWatcher = null;
            }
        }
        
        private async void ReceiveStringLoop(DataReader chatReader)
        {
            try
            {
                uint size = await chatReader.LoadAsync(sizeof(uint));
                if (size < sizeof(uint))
                {
                    SenderDisconnect("Remote device terminated connection - make sure only one instance of server is running on remote device");
                    return;
                }

                uint stringLength = chatReader.ReadUInt32();
                uint actualStringLength = await chatReader.LoadAsync(stringLength);
                if (actualStringLength != stringLength)
                {
                    // The underlying socket was closed before we were able to read the whole data
                    return;
                }

                ConversationList.Items.Add("Received: " + chatReader.ReadString(stringLength));

                ReceiveStringLoop(chatReader);
            }
            catch (Exception ex)
            {
                lock (this)
                {
                    if (chatSocket == null)
                    {
                        // Do not print anything here -  the user closed the socket.
                        if ((uint)ex.HResult == 0x80072745)
                            MessageBox.Show("Disconnect triggered by remote device");
                        else if ((uint)ex.HResult == 0x800703E3)
                            MessageBox.Show("The I/O operation has been aborted because of either a thread exit or an application request.");
                    }
                    else
                    {
                        SenderDisconnect("Read stream failed with error: " + ex.Message);
                    }
                }
            }
        }

        BluetoothReceiver()
        {
            ResultCollection = new ObservableCollection<RfcommChatDeviceDisplay>();
        }

        /// <summary>
        /// ユーザーが実行ボタンを押すと、近くにあるすべてのペアのないデバイスを照会します
        /// この場合、ペアになる前に、他のデバイスがRfcomm Chat Serverを実行している必要があります。
        /// </summary>
        public void Run()
        {
            if (deviceWatcher == null)
            {
                SetDeviceWatcherUI();
                StartUnpairedDeviceWatcher();
            }
            else
            {
                ResetMainUI();
            }
        }

        /// <summary>
        /// ユーザーが接続先のデバイスを選択すると呼び出されます。
        /// </summary>
        public void Connect()
        {
            // 最初にユーザーがデバイスを選択したことを確認する
            if (ResultsListView.SelectedItem != null)
            {
                Console.WriteLine("Connecting to remote device.Please wait...");
            }
            else
            {
                Console.WriteLine("Please select an item to connect to");
                return;
            }

            RfcommChatDeviceDisplay deviceInfoDisp = ResultCollection[ResultsListView.SelectedIndex] as RfcommChatDeviceDisplay;
            
            // デバイスを取得する前に、デバイスのアクセスチェックを実行します. 
            // まず、ユーザーが明示的に同意を拒否したかどうかを確認します。
            //DeviceAccessStatus accessStatus = DeviceAccessInformation.CreateFromId(deviceInfoDisp.Id).CurrentStatus;
            //if (accessStatus == DeviceAccessStatus.DeniedByUser)
            //{
            //    Console.WriteLine("This app does not have access to connect to the remote device (please grant access in Settings > Privacy > Other Devices");
            //    return;
            //}

            // そうでない場合は、Bluetoothデバイスを取得してみてください
            try
            {
                Console.WriteLine("000");
                bluetoothDevice = await BluetoothDevice.FromIdAsync(deviceInfoDisp.Id);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                ResetMainUI();
                return;
            }
            Console.WriteLine(0);
            // 有効なBluetoothデバイスオブジェクトを取得できなかった場合は、
            // 対になっていないすべてのデバイスと対話しないようにユーザーが指定している可能性があります。
            if (bluetoothDevice == null)
            {
                //Console.WriteLine("Bluetooth Device returned null.Access Status = " + accessStatus.ToString());
            }

            // これにより、キャッシュされていないBluetoothサービスのリストが返されるはずです
            //(したがって、ペアになったときにサーバがアクティブでなかった場合は、この呼び出しによって引き続き検出されます)
            Console.WriteLine(1);
            var rfcommServices = await bluetoothDevice.GetRfcommServicesForIdAsync(
                RfcommServiceId.FromUuid(Constants.RfcommChatServiceUuid), BluetoothCacheMode.Uncached);

            if (rfcommServices.Services.Count > 0)
            {
                Console.WriteLine("1 - 1");
                chatService = rfcommServices.Services[0];
            }
            else
            {
                Console.WriteLine("1-2");
                //rootPage.NotifyUser("Could not discover the chat service on the remote device",NotifyType.StatusMessage);
                ResetMainUI();
                return;
            }
            Console.WriteLine(2);
            // Do various checks of the SDP record to make sure you are talking to a device that actually supports the Bluetooth Rfcomm Chat Service
            var attributes = await chatService.GetSdpRawAttributesAsync();
            if (!attributes.ContainsKey(Constants.SdpServiceNameAttributeId))
            {
                //rootPage.NotifyUser("The Chat service is not advertising the Service Name attribute (attribute id=0x100). " +"Please verify that you are running the BluetoothRfcommChat server.",NotifyType.ErrorMessage);
                ResetMainUI();
                return;
            }
            Console.WriteLine(3);
            var attributeReader = DataReader.FromBuffer(attributes[Constants.SdpServiceNameAttributeId]);
            var attributeType = attributeReader.ReadByte();
            if (attributeType != Constants.SdpServiceNameAttributeType)
            {
                //rootPage.NotifyUser("The Chat service is using an unexpected format for the Service Name attribute. " +"Please verify that you are running the BluetoothRfcommChat server.",NotifyType.ErrorMessage);
                ResetMainUI();
                return;
            }
            Console.WriteLine(4);
            var serviceNameLength = attributeReader.ReadByte();

            // The Service Name attribute requires UTF-8 encoding.
            attributeReader.UnicodeEncoding = UnicodeEncoding.Utf8;
            Console.WriteLine(5);
            StopWatcher();

            lock (this)
            {
                Console.WriteLine("5-1");
                chatSocket = new StreamSocket();
            }
            Console.WriteLine(6);
            try
            {
                Console.WriteLine("6-1");
                await chatSocket.ConnectAsync(chatService.ConnectionHostName, chatService.ConnectionServiceName);

                SetChatUI(attributeReader.ReadString(serviceNameLength), bluetoothDevice.Name);
                chatWriter = new DataWriter(chatSocket.OutputStream);

                DataReader chatReader = new DataReader(chatSocket.InputStream);
                Console.WriteLine("6-2");
                ReceiveStringLoop(chatReader);
                Console.WriteLine(7);
            }
            catch (Exception ex) when ((uint)ex.HResult == 0x80070490) // ERROR_ELEMENT_NOT_FOUND
            {
                //rootPage.NotifyUser("Please verify that you are running the BluetoothRfcommChat server.", NotifyType.ErrorMessage);
                ResetMainUI();
            }
            catch (Exception ex) when ((uint)ex.HResult == 0x80072740) // WSAEADDRINUSE
            {
                //rootPage.NotifyUser("Please verify that there is no other RFCOMM connection to the same device.", NotifyType.ErrorMessage);
                ResetMainUI();
            }

        }

        /// <summary>
        /// Cleans up the socket and DataWriter and reset the UI
        /// </summary>
        public void Disconnect(string disconnectReason)
        {
            if (chatWriter != null)
            {
                chatWriter.DetachStream();
                chatWriter = null;
            }


            if (chatService != null)
            {
                chatService.Dispose();
                chatService = null;
            }
            lock (this)
            {
                if (chatSocket != null)
                {
                    chatSocket.Dispose();
                    chatSocket = null;
                }
            }

            Console.WriteLine(disconnectReason);
        }

        /// <summary>
        /// Takes the contents of the MessageTextBox and writes it to the outgoing chatWriter
        /// </summary>
        public void Send()
        {
            try
            {
                if (SenderMessageTextBox.Text.Length != 0)
                {
                    chatWriter.WriteUInt32((uint)SenderMessageTextBox.Text.Length);
                    chatWriter.WriteString(SenderMessageTextBox.Text);

                    ConversationList.Items.Add("Sent: " + SenderMessageTextBox.Text);
                    SenderMessageTextBox.Text = "";
                    await chatWriter.StoreAsync();

                }
            }
            catch (Exception ex) when ((uint)ex.HResult == 0x80072745)
            {
                // The remote device has disconnected the connection
                //rootPage.NotifyUser("Remote side disconnect: " + ex.HResult.ToString() + " - " + ex.Message,NotifyType.StatusMessage);
            }
        }

        public class RfcommChatDeviceDisplay : INotifyPropertyChanged
        {
            private DeviceInformation deviceInfo;

            public RfcommChatDeviceDisplay(DeviceInformation deviceInfoIn)
            {
                deviceInfo = deviceInfoIn;
            }

            public DeviceInformation DeviceInformation
            {
                get
                {
                    return deviceInfo;
                }

                private set
                {
                    deviceInfo = value;
                }
            }

            public string Id
            {
                get
                {
                    return deviceInfo.Id;
                }
            }

            public string Name
            {
                get
                {
                    return deviceInfo.Name;
                }
            }

            public void Update(DeviceInformationUpdate deviceInfoUpdate)
            {
                deviceInfo.Update(deviceInfoUpdate);
            }

            public event PropertyChangedEventHandler PropertyChanged;
            protected void OnPropertyChanged(string name)
            {
                PropertyChangedEventHandler handler = PropertyChanged;
                if (handler != null)
                {
                    handler(this, new PropertyChangedEventArgs(name));
                }
            }

        }

        */
    }
}
