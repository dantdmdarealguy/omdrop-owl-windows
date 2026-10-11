using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth.Advertisement;

namespace Omdrop;

/// <summary>
/// The Bluetooth LE advert that wakes AirDrop on a nearby Apple device: Apple
/// manufacturer data (company 0x004C) holding one Continuity record, type 0x05
/// (AirDrop), 18 bytes. An iPhone publishes its _airdrop._tcp over AWDL only
/// once it hears one, so a sender must advertise while it browses.
///
///   iOS 26 shape: 40 &lt;3 bytes&gt; | 00 00 00 00 | 03 | 4 x 2-byte contact hashes | 00
///   older shape:  00 x 8                       | 01 | 4 x 2-byte contact hashes | 00
///
/// Zero hashes say "no contact match", which wakes a device set to Everyone
/// (Contacts Only needs our Apple ID's hashes; not done). Layouts as read off
/// the air by omdrop-awdl.
/// </summary>
public sealed class AirDropAdvert : IDisposable
{
    const ushort Apple = 0x004C;
    readonly BluetoothLEAdvertisementPublisher publisher;

    public AirDropAdvert(bool legacyShape = false)
    {
        byte[] body = legacyShape
            ? [0, 0, 0, 0, 0, 0, 0, 0, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0]
            : [0x40, 0, 0, 0, 0, 0, 0, 0, 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        byte[] tlv = [0x05, (byte)body.Length, .. body];
        var adv = new BluetoothLEAdvertisement();
        adv.ManufacturerData.Add(new BluetoothLEManufacturerData(Apple, tlv.AsBuffer()));
        publisher = new BluetoothLEAdvertisementPublisher(adv);
        publisher.StatusChanged += (_, e) =>
        {
            if (e.Status == BluetoothLEAdvertisementPublisherStatus.Aborted)
                Log.Warn($"ble: Windows stopped the AirDrop advert ({e.Error})");
            else
                Log.Debug($"ble: advert {e.Status}");
        };
    }

    public BluetoothLEAdvertisementPublisherStatus Status => publisher.Status;

    public void Start()
    {
        publisher.Start();
        Log.Info("ble: advertising AirDrop (wakes Apple devices set to Everyone)");
    }

    public void Dispose()
    {
        if (publisher.Status is BluetoothLEAdvertisementPublisherStatus.Started or BluetoothLEAdvertisementPublisherStatus.Waiting)
            publisher.Stop();
    }
}
