using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WpfBluetoothSample
{
    class Constants
    {
        // チャットサーバーのカスタムサービスUuid：34B1CF4D-1069-4AD6-89B6-E161D79BE4D8
        public static readonly Guid RfcommChatServiceUuid = Guid.Parse("34B1CF4D-1069-4AD6-89B6-E161D79BE4D8");

        // サービス名のID SDP属性
        public const UInt16 SdpServiceNameAttributeId = 0x100;

        // サービス名SDP属性のSDPタイプ。
        // SDP属性の最初のバイトは、次のようにSDP属性タイプをエンコードします。
        // - 最下位3ビットのAttribute Typeサイズ。
        // - 上位5ビットのSDP属性タイプ値。
        public const byte SdpServiceNameAttributeType = (4 << 3) | 5;

        // サービス名SDP属性の値
        public const string SdpServiceName = "Bluetooth Rfcomm Chat Service";
    }
}
