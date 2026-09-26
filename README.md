### com0com.Redirector
This is a GUI app that allows for convenient access to com2tcp/hub4com.  Simply select the serial port, enter your endpoint details, select the mode (TCPClient, UDP, and RFC2217 supported presently) and click start.

You can enter preset endpoints into the portsdb.txt file (C:\ProgramData\com0comRedirector\portsdb.txt by default, see PortsDBLocation in Com0com.Redirector.exe.config), in CSV format "Name, [UDP|TCPClient|RFC2217], RemoteIP, RemotePort, LocalPort"

### COM Bridge
The COM Bridge section connects two existing COM ports to each other (COM-A to COM-B and COM-B to COM-A) with hub4com:

    hub4com.exe --octs=off --route=All:All \\.\COM10 \\.\COM20

Select Left COM and Right COM (the list comes from Windows; use Refresh to update it) and click Connect. Disconnect, or closing the app, stops the hub4com it started; hub4com processes started by anything else are never touched.

The COM Bridge only uses ports that already exist. It never creates, removes or renames ports and does not need administrator rights, so an administrator can create the com0com port pairs once and normal users can switch the routing.

hub4com.exe is looked up at the Hub4ComPath setting in Com0com.Redirector.exe.config (C:\Program Files (x86)\com0com\hub4com.exe by default), then next to Com0com.Redirector.exe.

COM Bridge presets can be added to portsdb.txt as "Name,COMBridge,LeftCOM,RightCOM", for example:

    VP6800,COMBridge,COM3,COM20
    QRReader,COMBridge,COM4,COM21

These four-column lines are ignored by the TCPClient/UDP/RFC2217 preset list, and the five-column lines are ignored by the COM Bridge.
