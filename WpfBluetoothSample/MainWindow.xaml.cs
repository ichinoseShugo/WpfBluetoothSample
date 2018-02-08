using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace WpfBluetoothSample
{
    /// <summary>
    /// MainWindow.xaml の相互作用ロジック
    /// </summary>
    public partial class MainWindow : Window
    {
        private StreamSocket receiver_socket;
        private DataWriter receiver_writer;
        private RfcommServiceProvider receiver_rfcommProvider;
        private StreamSocketListener receiver_socketListener;

        MidiManager midiManager = new MidiManager();

        public MainWindow()
        {
            this.InitializeComponent();
            InitializeSender();
        }

        private void playMidi_Click(object sender, RoutedEventArgs e)
        {
            midiManager.playMidi();
        }

        private async void Beep()
        {

        }

        private void ListenButton_Click(object sender, RoutedEventArgs e)
        {
            InitializeRfcommServer();
        }

        /// <summary>
        /// RfcommServiceProviderを使用してサーバーを初期化し、
        /// チャットサービスのUUIDを宣言し、着信接続の受信を開始します。
        /// </summary>
        private async void InitializeRfcommServer()
        {
            ReceiverListenButton.IsEnabled = false;
            ReceiverDisconnectButton.IsEnabled = true;

            try
            {
                receiver_rfcommProvider = await RfcommServiceProvider.CreateAsync(
                    RfcommServiceId.FromUuid(Constants.RfcommChatServiceUuid));
            }
            // 例外HRESULT_FROM_WIN32（ERROR_DEVICE_NOT_AVAILABLE）をキャッチします。
            catch (Exception e) when ((uint)e.HResult == 0x800710DF)
            {
                Console.WriteLine("error!:" + e.Message);
                // Bluetoothラジオがオフになっている可能性があります。
                ReceiverListenButton.IsEnabled = true;
                ReceiverDisconnectButton.IsEnabled = false;
                return;
            }

            // このサービスのリスナーを作成してリスンを開始する
            receiver_socketListener = new StreamSocketListener();
            receiver_socketListener.ConnectionReceived += OnConnectionReceived;
            var rfcomm = receiver_rfcommProvider.ServiceId.AsString();

            await receiver_socketListener.BindServiceNameAsync(receiver_rfcommProvider.ServiceId.AsString(),
                SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);

            // SDP属性を設定し、Bluetooth広告を開始する
            InitializeServiceSdpAttributes(receiver_rfcommProvider);

            try
            {
                receiver_rfcommProvider.StartAdvertising(receiver_socketListener);
            }
            catch (Exception e)
            {
                // RfcommServiceProviderへの参照を取得できない場合は、その理由をユーザーに伝えます。
                // 通常、ユーザーが自分のプライバシー設定を変更してデバイスとの同期を防止すると例外がスローされます。
                Console.WriteLine("error!:" + e.Message);
                ReceiverListenButton.IsEnabled = true;
                ReceiverDisconnectButton.IsEnabled = false;
                return;
            }
        }

        /// <summary>
        /// ペアリングが発生したときにクライアントデバイスに表示されるSDPレコードを作成します。 
        /// </summary>
        /// <param name="rfcommProvider">サーバーの初期化に使用されているRfcommServiceProvider</param>
        private void InitializeServiceSdpAttributes(RfcommServiceProvider rfcommProvider)
        {
            var sdpWriter = new DataWriter();

            // Write the Service Name Attribute.
            sdpWriter.WriteByte(Constants.SdpServiceNameAttributeType);

            // The length of the UTF-8 encoded Service Name SDP Attribute.
            sdpWriter.WriteByte((byte)Constants.SdpServiceName.Length);

            // The UTF-8 encoded Service Name value.
            sdpWriter.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;
            sdpWriter.WriteString(Constants.SdpServiceName);

            // Set the SDP Attribute on the RFCOMM Service Provider.
            rfcommProvider.SdpRawAttributes.Add(Constants.SdpServiceNameAttributeId, sdpWriter.DetachBuffer());
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            SendMessage();
        }

        private async void SendMessage()
        {
            // There's no need to send a zero length message
            if (ReceiverMessageTextBox.Text.Length != 0)
            {
                // Make sure that the connection is still up and there is a message to send
                if (receiver_socket != null)
                {
                    string message = ReceiverMessageTextBox.Text;
                    receiver_writer.WriteUInt32((uint)message.Length);
                    receiver_writer.WriteString(message);

                    ReceiverConversationListBox.Items.Add("Sent: " + message);
                    // Clear the messageTextBox for a new message
                    ReceiverMessageTextBox.Text = "";

                    await receiver_writer.StoreAsync();
                }
            }
        }

        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            Disconnect();
        }

        private async void Disconnect()
        {
            if (receiver_rfcommProvider != null)
            {
                receiver_rfcommProvider.StopAdvertising();
                receiver_rfcommProvider = null;
            }

            if (receiver_socketListener != null)
            {
                receiver_socketListener.Dispose();
                receiver_socketListener = null;
            }

            if (receiver_writer != null)
            {
                receiver_writer.DetachStream();
                receiver_writer = null;
            }

            if (receiver_socket != null)
            {
                receiver_socket.Dispose();
                receiver_socket = null;
            }
            await Dispatcher.BeginInvoke(new Action(() =>
            {
                ReceiverListenButton.IsEnabled = true;
                ReceiverDisconnectButton.IsEnabled = false;
                ReceiverConversationListBox.Items.Clear();
            }));
        }

        /// <summary>
        /// ソケットリスナーが着信Bluetooth接続を受け入れると呼び出されます。
        /// </summary>
        /// <param name="sender">接続を受け入れたソケットリスナー。</param>
        /// <param name="args">接続は、接続されたソケットを含むパラメータを受け入れます。</param>
        private async void OnConnectionReceived(
            StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            // Don't need the listener anymore
            receiver_socketListener.Dispose();
            receiver_socketListener = null;

            try
            {
                receiver_socket = args.Socket;
            }
            catch (Exception e)
            {
                Disconnect();
                return;
            }

            // Note - this is the supported way to get a Bluetooth device from a given socket
            var remoteDevice = await BluetoothDevice.FromHostNameAsync(receiver_socket.Information.RemoteHostName);

            receiver_writer = new DataWriter(receiver_socket.OutputStream);
            var reader = new DataReader(receiver_socket.InputStream);
            bool remoteDisconnection = false;

            // Infinite read buffer loop
            while (true)
            {
                try
                {
                    // Based on the protocol we've defined, the first uint is the size of the message
                    uint readLength = await reader.LoadAsync(sizeof(uint));

                    // Check if the size of the data is expected (otherwise the remote has already terminated the connection)
                    if (readLength < sizeof(uint))
                    {
                        remoteDisconnection = true;
                        break;
                    }
                    uint currentLength = reader.ReadUInt32();

                    // Load the rest of the message since you already know the length of the data expected.  
                    readLength = await reader.LoadAsync(currentLength);

                    // Check if the size of the data is expected (otherwise the remote has already terminated the connection)
                    if (readLength < currentLength)
                    {
                        remoteDisconnection = true;
                        break;
                    }
                    string message = reader.ReadString(currentLength);

                    midiManager.playMidi();
                    await Dispatcher.BeginInvoke(new Action(() =>
                    {
                        ReceiverConversationListBox.Items.Add("Received: " + message);
                    }));
                }
                // Catch exception HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED).
                catch (Exception e) when ((uint)e.HResult == 0x800703E3)
                {
                    Console.WriteLine("error!:" + e.Message);
                    break;
                }
            }

            reader.DetachStream();
            if (remoteDisconnection)
            {
                Disconnect();
            }
        }


        //-----------------------------------------------------------------------------------------

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

        private void InitializeSender()
        {
            ResultCollection = new ObservableCollection<RfcommChatDeviceDisplay>();
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

        /// <summary>
        /// ユーザーが実行ボタンを押すと、近くにあるすべてのペアのないデバイスを照会します
        /// この場合、ペアになる前に、他のデバイスがRfcomm Chat Serverを実行している必要があります。
        /// </summary>
        /// <param name="sender">イベントをトリガしたインスタンス。</param>
        /// <param name="e">イベントにつながった条件を説明するイベントデータ。</param>
        private void RunButton_Click(object sender, RoutedEventArgs e)
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
        /// ユーザーが2回実行できないように、非同期操作を実行中にボタンを無効にします。
        /// </summary>
        private void SetDeviceWatcherUI()
        {
            RunButton.Content = "Stop";
            RunButton.IsEnabled = false;
            ResultsListView.Visibility = Visibility.Visible;
            ResultsListView.IsEnabled = true;
        }
        
        private void StartUnpairedDeviceWatcher()
        {
            // その他のプロパティのリクエスト
            string[] requestedProperties = new string[] {
                "System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected" };

            deviceWatcher = DeviceInformation.CreateWatcher(
                "(System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\")",
                requestedProperties, DeviceInformationKind.AssociationEndpoint);

            // ウォッチャーを開始する前にウォッチャーイベントのハンドラーを接続する
            deviceWatcher.Added += new TypedEventHandler<DeviceWatcher, DeviceInformation>(async (watcher, deviceInfo) =>
            {
                await Dispatcher.BeginInvoke(new Action(() =>
                {
                    // デバイス名が空白でないことを確認する
                    if (deviceInfo.Name != "")
                    {
                        ResultCollection.Add(new RfcommChatDeviceDisplay(deviceInfo));
                        Console.WriteLine(ResultCollection.Count + " devices found");
                        Console.WriteLine(ResultCollection[ResultCollection.Count-1].Name);
                    }
                }));
                RefleshList();
            });

            deviceWatcher.Updated += new TypedEventHandler<DeviceWatcher, DeviceInformationUpdate>(async (watcher, deviceInfoUpdate) =>
            {
                Console.WriteLine("dw update");
                await Dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (RfcommChatDeviceDisplay rfcommInfoDisp in ResultCollection)
                    {
                        if (rfcommInfoDisp.Id == deviceInfoUpdate.Id)
                        {
                            rfcommInfoDisp.Update(deviceInfoUpdate);
                            break;
                        }
                    }
                }));
                RefleshList();
            });

            deviceWatcher.EnumerationCompleted += new TypedEventHandler<DeviceWatcher, Object>(async (watcher, obj) =>
            {
                Console.WriteLine("dw enum");
                await Dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (RfcommChatDeviceDisplay rfcommInfoDisp in ResultCollection)
                    {
                        Console.WriteLine(ResultCollection.Count + "devices found. Enumeration completed. Watching for updates...");
                    }
                }));
                RefleshList();
            });

            deviceWatcher.Removed += new TypedEventHandler<DeviceWatcher, DeviceInformationUpdate>(async (watcher, deviceInfoUpdate) =>
            {
                Console.WriteLine("dw remove");
                // UI要素にバインドされたコレクションデータがあるので、UIスレッドでコレクションを更新する必要があります。
                await Dispatcher.BeginInvoke(new Action(() =>
                {
                    // コレクション内の対応するDeviceInformationを見つけて削除します
                    foreach (RfcommChatDeviceDisplay rfcommInfoDisp in ResultCollection)
                    {
                        if (rfcommInfoDisp.Id == deviceInfoUpdate.Id)
                        {
                            ResultCollection.Remove(rfcommInfoDisp);
                            break;
                        }
                    }
                    Console.WriteLine(ResultCollection.Count + "devices found.");
                }));
                RefleshList();
            });

            deviceWatcher.Stopped += new TypedEventHandler<DeviceWatcher, Object>(async (watcher, obj) =>
            {
                Console.WriteLine("dw stop");
                await Dispatcher.BeginInvoke(new Action(() =>
                {
                        ResultCollection.Clear();
                }));
                RefleshList();
            });

            deviceWatcher.Start();
        }

        async private void RefleshList()
        {
            await Dispatcher.BeginInvoke(new Action(() =>
            {
                string[] names = new string[ResultCollection.Count];
                for (int i = 0; i < ResultCollection.Count; i++) names[i] = ResultCollection[i].Name;
                ResultsListView.ItemsSource = names;
            }));
        }

        private void ResetMainUI()
        {
            RunButton.Content = "Start";
            RunButton.IsEnabled = true;
            ConnectButton.Visibility = Visibility.Visible;
            ResultsListView.Visibility = Visibility.Visible;
            ResultsListView.IsEnabled = true;

            // Re-set device specific UX
            ChatBox.Visibility = Visibility.Collapsed;
            if (ConversationList.Items != null) ConversationList.Items.Clear();
            StopWatcher();
        }

        /// <summary>
        /// ユーザーが接続先のデバイスを選択すると呼び出されます。
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
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
            //Console.WriteLine(deviceInfoDisp.Id);
            //Console.WriteLine(deviceInfoDisp.DeviceInformation);
            
            /*
            // デバイスを取得する前に、デバイスのアクセスチェックを実行します. 
            // まず、ユーザーが明示的に同意を拒否したかどうかを確認します。
            DeviceAccessStatus accessStatus = DeviceAccessInformation.CreateFromId(deviceInfoDisp.Id).CurrentStatus;
            if (accessStatus == DeviceAccessStatus.DeniedByUser)
            {
                Console.WriteLine("This app does not have access to connect to the remote device (please grant access in Settings > Privacy > Other Devices");
                return;
            }
            */

            // そうでない場合は、Bluetoothデバイスを取得してみてください
            try
            {
                bluetoothDevice = await BluetoothDevice.FromIdAsync(deviceInfoDisp.Id);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                ResetMainUI();
                return;
            }

            // 有効なBluetoothデバイスオブジェクトを取得できなかった場合は、
            // 対になっていないすべてのデバイスと対話しないようにユーザーが指定している可能性があります。
            if (bluetoothDevice == null)
            {
                //Console.WriteLine("Bluetooth Device returned null.Access Status = " + accessStatus.ToString());
            }

            // これにより、キャッシュされていないBluetoothサービスのリストが返されるはずです
            //(したがって、ペアになったときにサーバがアクティブでなかった場合は、この呼び出しによって引き続き検出されます)
            var rfcommServices = await bluetoothDevice.GetRfcommServicesForIdAsync(
                RfcommServiceId.FromUuid(Constants.RfcommChatServiceUuid), BluetoothCacheMode.Uncached);

            if (rfcommServices.Services.Count > 0)
            {
                chatService = rfcommServices.Services[0];
            }
            else
            {
                //rootPage.NotifyUser("Could not discover the chat service on the remote device",NotifyType.StatusMessage);
                ResetMainUI();
                return;
            }

            // Bluetooth Rfcommチャットサービスを実際にサポートしているデバイスと通信していることを確認するために
            // SDPレコードのさまざまなチェックを行います
            var attributes = await chatService.GetSdpRawAttributesAsync();
            if (!attributes.ContainsKey(Constants.SdpServiceNameAttributeId))
            {
                ResetMainUI();
                return;
            }
            var attributeReader = DataReader.FromBuffer(attributes[Constants.SdpServiceNameAttributeId]);
            var attributeType = attributeReader.ReadByte();
            if (attributeType != Constants.SdpServiceNameAttributeType)
            {
                //rootPage.NotifyUser("The Chat service is using an unexpected format for the Service Name attribute. " +"Please verify that you are running the BluetoothRfcommChat server.",NotifyType.ErrorMessage);
                ResetMainUI();
                return;
            }
            var serviceNameLength = attributeReader.ReadByte();

            // The Service Name attribute requires UTF-8 encoding.
            attributeReader.UnicodeEncoding = UnicodeEncoding.Utf8;
            StopWatcher();

            lock (this)
            {
                chatSocket = new StreamSocket();
            }
            try
            {
                await chatSocket.ConnectAsync(chatService.ConnectionHostName, chatService.ConnectionServiceName);

                SetChatUI(attributeReader.ReadString(serviceNameLength), bluetoothDevice.Name);
                chatWriter = new DataWriter(chatSocket.OutputStream);

                DataReader chatReader = new DataReader(chatSocket.InputStream);
                Console.WriteLine("run loop");
                ReceiveStringLoop(chatReader);
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

        private void SenderSendButton_Click(object sender, RoutedEventArgs e)
        {
            SenderSendMessage();
        }

        /// <summary>
        /// Takes the contents of the MessageTextBox and writes it to the outgoing chatWriter
        /// </summary>
        private async void SenderSendMessage()
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
                    midiManager.playMidi();
                }
            }
            catch (Exception ex) when ((uint)ex.HResult == 0x80072745)
            {
                // The remote device has disconnected the connection
                //rootPage.NotifyUser("Remote side disconnect: " + ex.HResult.ToString() + " - " + ex.Message,NotifyType.StatusMessage);
            }
        }

        private async void ReceiveStringLoop(DataReader chatReader)
        {
            Console.WriteLine(0);
            try
            {
                uint size = await chatReader.LoadAsync(sizeof(uint));
                Console.WriteLine("size");
                Console.WriteLine(size);
                Console.WriteLine(sizeof(uint));
                // リモートデバイスによる接続の切断
                // リモートデバイス上で1つのサーバインスタンスしか実行されていないことを確認する
                if (size < sizeof(uint))
                {
                    SenderDisconnect("error");
                    return;
                }

                uint stringLength = chatReader.ReadUInt32();
                uint actualStringLength = await chatReader.LoadAsync(stringLength);
                if (actualStringLength != stringLength)
                {
                    // データ全体を読み取ることができる前に、下にあるソケットが閉じられました
                    return;
                }

                ConversationList.Items.Add("Received: " + chatReader.ReadString(stringLength));

                ReceiveStringLoop(chatReader);
            }
            catch (Exception ex)
            {
                Console.WriteLine("catch");
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

        private void SenderDisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            SenderDisconnect("Disconnected");
        }

        /// <summary>
        /// Cleans up the socket and DataWriter and reset the UI
        /// </summary>
        /// <param name="disconnectReason"></param>
        private void SenderDisconnect(string disconnectReason)
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
            ResetMainUI();
        }

        private void SetChatUI(string serviceName, string deviceName)
        {
            Console.WriteLine("connect");
            ServiceName.Text = "Service Name: " + serviceName;
            DeviceName.Text = "Connected to: " + deviceName;
            RunButton.IsEnabled = false;
            ConnectButton.Visibility = Visibility.Collapsed;
            ResultsListView.IsEnabled = false;
            ResultsListView.Visibility = Visibility.Collapsed;
            ChatBox.Visibility = Visibility.Visible;
        }

        private void ResultsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
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

        private void SenderMessageTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {

        }



        //-------------------------------------------------------------------------------------------
        private void s1_Click(object sender, RoutedEventArgs e)
        {

        }

        private void s2_Click(object sender, RoutedEventArgs e)
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
        }

        private void s3_Click(object sender, RoutedEventArgs e)
        {

        }

        private void r1_Click(object sender, RoutedEventArgs e)
        {

        }

        private void r2_Click(object sender, RoutedEventArgs e)
        {

        }

        private void r3_Click(object sender, RoutedEventArgs e)
        {

        }

        private void r4_Click(object sender, RoutedEventArgs e)
        {

        }
    }
}
