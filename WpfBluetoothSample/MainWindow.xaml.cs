using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
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

        public MainWindow()
        {
            this.InitializeComponent();
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
            ListenButton.IsEnabled = false;
            DisconnectButton.IsEnabled = true;

            try
            {
                receiver_rfcommProvider = await RfcommServiceProvider.CreateAsync(
                    RfcommServiceId.FromUuid(Constants.RfcommChatServiceUuid));
            }
            // 例外HRESULT_FROM_WIN32（ERROR_DEVICE_NOT_AVAILABLE）をキャッチします。
            catch (Exception ex) when ((uint)ex.HResult == 0x800710DF)
            {
                // Bluetoothラジオがオフになっている可能性があります。
                ListenButton.IsEnabled = true;
                DisconnectButton.IsEnabled = false;
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
                ListenButton.IsEnabled = true;
                DisconnectButton.IsEnabled = false;
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
            if (MessageTextBox.Text.Length != 0)
            {
                // Make sure that the connection is still up and there is a message to send
                if (receiver_socket != null)
                {
                    string message = MessageTextBox.Text;
                    receiver_writer.WriteUInt32((uint)message.Length);
                    receiver_writer.WriteString(message);

                    ConversationListBox.Items.Add("Sent: " + message);
                    // Clear the messageTextBox for a new message
                    MessageTextBox.Text = "";

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
            await Dispatcher.BeginInvoke(new Action(() =>{
                ListenButton.IsEnabled = true;
                DisconnectButton.IsEnabled = false;
                ConversationListBox.Items.Clear();
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
                    
                    await Dispatcher.BeginInvoke(new Action(() => {
                        ConversationListBox.Items.Add("Received: " + message);
                    }));
                }
                // Catch exception HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED).
                catch (Exception ex) when ((uint)ex.HResult == 0x800703E3)
                {
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

        private StreamSocket sendersocket;
        private DataWriter senderwriter;
        private RfcommServiceProvider senderrfcommProvider;
        private StreamSocketListener sendersocketListener;

        private void senderListenButton_Click(object sender, RoutedEventArgs e)
        {
            senderInitializeRfcommServer();
        }

        /// <summary>
        /// Initializes the server using RfcommServiceProvider to advertise the Chat Service UUID and start listening
        /// for incoming connections.
        /// </summary>
        private async void senderInitializeRfcommServer()
        {
            senderListenButton.IsEnabled = false;
            senderDisconnectButton.IsEnabled = true;

            try
            {
                senderrfcommProvider = await RfcommServiceProvider.CreateAsync(RfcommServiceId.FromUuid(Constants.RfcommChatServiceUuid));
            }
            // Catch exception HRESULT_FROM_WIN32(ERROR_DEVICE_NOT_AVAILABLE).
            catch (Exception ex) when ((uint)ex.HResult == 0x800710DF)
            {
                // The Bluetooth radio may be off.
                senderListenButton.IsEnabled = true;
                senderDisconnectButton.IsEnabled = false;
                return;
            }


            // Create a listener for this service and start listening
            sendersocketListener = new StreamSocketListener();
            sendersocketListener.ConnectionReceived += senderOnConnectionReceived;
            var rfcomm = senderrfcommProvider.ServiceId.AsString();

            await sendersocketListener.BindServiceNameAsync(senderrfcommProvider.ServiceId.AsString(),
                SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);

            // Set the SDP attributes and start Bluetooth advertising
            senderInitializeServiceSdpAttributes(senderrfcommProvider);

            try
            {
                senderrfcommProvider.StartAdvertising(sendersocketListener);
            }
            catch (Exception e)
            {
                // If you aren't able to get a reference to an RfcommServiceProvider, tell the user why. 
                // Usually throws an exception if user changed their privacy settings to prevent Sync w/ Devices. 
                senderListenButton.IsEnabled = true;
                senderDisconnectButton.IsEnabled = false;
                return;
            }
        }

        /// <summary>
        /// Creates the SDP record that will be revealed to the Client device when pairing occurs.  
        /// </summary>
        /// <param name="rfcommProvider">The RfcommServiceProvider that is being used to initialize the server</param>
        private void senderInitializeServiceSdpAttributes(RfcommServiceProvider rfcommProvider)
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

        private void senderSendButton_Click(object sender, RoutedEventArgs e)
        {
            senderSendMessage();
        }

        private async void senderSendMessage()
        {
            // There's no need to send a zero length message
            if (MessageTextBox.Text.Length != 0)
            {
                // Make sure that the connection is still up and there is a message to send
                if (sendersocket != null)
                {
                    string message = MessageTextBox.Text;
                    senderwriter.WriteUInt32((uint)message.Length);
                    senderwriter.WriteString(message);

                    ConversationListBox.Items.Add("Sent: " + message);
                    // Clear the messageTextBox for a new message
                    MessageTextBox.Text = "";

                    await senderwriter.StoreAsync();

                }
            }
        }


        private void senderDisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            senderDisconnect();
        }

        private async void senderDisconnect()
        {
            if (senderrfcommProvider != null)
            {
                senderrfcommProvider.StopAdvertising();
                senderrfcommProvider = null;
            }

            if (sendersocketListener != null)
            {
                sendersocketListener.Dispose();
                sendersocketListener = null;
            }

            if (senderwriter != null)
            {
                senderwriter.DetachStream();
                senderwriter = null;
            }

            if (sendersocket != null)
            {
                sendersocket.Dispose();
                sendersocket = null;
            }
            await Dispatcher.BeginInvoke(new Action(() => {
                senderListenButton.IsEnabled = true;
                senderDisconnectButton.IsEnabled = false;
                senderConversationListBox.Items.Clear();
            }));
        }

        /// <summary>
        /// Invoked when the socket listener accepts an incoming Bluetooth connection.
        /// </summary>
        /// <param name="sender">The socket listener that accepted the connection.</param>
        /// <param name="args">The connection accept parameters, which contain the connected socket.</param>
        private async void senderOnConnectionReceived(
            StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            // Don't need the listener anymore
            sendersocketListener.Dispose();
            sendersocketListener = null;

            try
            {
                sendersocket = args.Socket;
            }
            catch (Exception e)
            {
                senderDisconnect();
                return;
            }

            // Note - this is the supported way to get a Bluetooth device from a given socket
            var remoteDevice = await BluetoothDevice.FromHostNameAsync(sendersocket.Information.RemoteHostName);

            senderwriter = new DataWriter(sendersocket.OutputStream);
            var reader = new DataReader(sendersocket.InputStream);
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

                    await Dispatcher.BeginInvoke(new Action(() => {
                        senderConversationListBox.Items.Add("Received: " + message);
                    }));
                }
                // Catch exception HRESULT_FROM_WIN32(ERROR_OPERATION_ABORTED).
                catch (Exception ex) when ((uint)ex.HResult == 0x800703E3)
                {
                    break;
                }
            }

            reader.DetachStream();
            if (remoteDisconnection)
            {
                senderDisconnect();
            }
        }

    }
}
