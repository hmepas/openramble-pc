namespace OpenRamble.LinuxInput.Tests;

/// <summary>
/// Telling the machine's keyboards from everything else that has keys.
/// </summary>
/// <remarks>
/// The fixtures are real blocks copied out of a running <c>/proc/bus/input/devices</c>, because the
/// classification is only worth anything against the devices people actually have: a power button
/// reports <c>EV_KEY</c>, a mouse reports sixteen buttons, and the media half of a keyboard is a
/// separate node with a key bitmap of its own. Every one of them would be opened and given a
/// thread by a check that only asked "does it have keys".
/// </remarks>
public sealed class InputDeviceCatalogTests
{
    /// <summary>A power button. One key, and it is not a keyboard.</summary>
    private const string PowerButton = """
        I: Bus=0019 Vendor=0000 Product=0001 Version=0000
        N: Name="Power Button"
        P: Phys=PNP0C0C/button/input0
        S: Sysfs=/devices/platform/PNP0C0C:00/input/input0
        U: Uniq=
        H: Handlers=kbd event0
        B: PROP=0
        B: EV=3
        B: KEY=8000 10000000000000 0
        """;

    /// <summary>A plain USB keyboard.</summary>
    private const string UsbKeyboard = """
        I: Bus=0003 Vendor=1c4f Product=0002 Version=0110
        N: Name="SIGMACHIP USB Keyboard"
        P: Phys=usb-0000:0a:00.4-1/input0
        S: Sysfs=/devices/pci0000:00/0000:0a:00.4/usb5/5-1/5-1:1.0/input/input2
        U: Uniq=
        H: Handlers=sysrq kbd leds event2
        B: PROP=0
        B: EV=120013
        B: KEY=1000000000007 ff800000000007ff febeffdff3cfffff fffffffffffffffe
        B: MSC=10
        B: LED=7
        """;

    /// <summary>The same keyboard's media keys, which are a device of their own.</summary>
    private const string ConsumerControl = """
        I: Bus=0003 Vendor=1c4f Product=0002 Version=0110
        N: Name="SIGMACHIP USB Keyboard Consumer Control"
        P: Phys=usb-0000:0a:00.4-1/input1
        S: Sysfs=/devices/pci0000:00/0000:0a:00.4/usb5/5-1/5-1:1.1/input/input3
        U: Uniq=
        H: Handlers=kbd event3
        B: PROP=0
        B: EV=1f
        B: KEY=733eff 0 0 483ffff17aff32d bfd4444600000000 1 130c730b17c000 267bfad9415fed 9e168000004400 10000002
        B: REL=1040
        B: ABS=100000000
        B: MSC=10
        """;

    /// <summary>A mouse. Sixteen buttons, none of them a letter.</summary>
    private const string Mouse = """
        I: Bus=0003 Vendor=046d Product=c24e Version=0111
        N: Name="Logitech G500s Laser Gaming Mouse"
        P: Phys=usb-0000:0b:00.0-1.1/input0
        S: Sysfs=/devices/pci0000:00/0000:0b:00.0/usb7/7-1/7-1.1/input/input5
        U: Uniq=A74B0546300009
        H: Handlers=event5 mouse0
        B: PROP=0
        B: EV=17
        B: KEY=ffff0000 0 0 0 0
        B: REL=1943
        B: MSC=10
        """;

    /// <summary>A remapper's virtual keyboard - and on a machine running one, the only one that
    /// still produces events, because it grabs the real device.</summary>
    private const string VirtualKeyboard = """
        I: Bus=0000 Vendor=0000 Product=0000 Version=0000
        N: Name="keyd virtual keyboard"
        P: Phys=
        S: Sysfs=/devices/virtual/input/input19
        U: Uniq=
        H: Handlers=sysrq kbd leds event17 rfkill
        B: PROP=0
        B: EV=120003
        B: KEY=10000000000000 0 ffffffffffffff0f ffffffffffffffff ffffffffffffffff fffffffffffffffe
        B: LED=1ff
        """;

    private static string Catalog(params string[] blocks) => string.Join("\n\n", blocks) + "\n\n";

    [Fact]
    public void AKeyboardIsFoundAndNamed()
    {
        IReadOnlyList<KeyboardDevice> keyboards = InputDeviceCatalog.Parse(Catalog(UsbKeyboard));

        KeyboardDevice only = Assert.Single(keyboards);
        Assert.Equal("SIGMACHIP USB Keyboard", only.Name);
        Assert.Equal("event2", only.EventNode);
        Assert.Equal("/dev/input/event2", only.Path);
    }

    [Fact]
    public void APowerButtonIsNotAKeyboard()
    {
        // It reports EV_KEY and one key. Opening it would cost a thread and a descriptor and
        // deliver nothing a dictation could use.
        Assert.Empty(InputDeviceCatalog.Parse(Catalog(PowerButton)));
    }

    [Fact]
    public void AMouseIsNotAKeyboard()
    {
        Assert.Empty(InputDeviceCatalog.Parse(Catalog(Mouse)));
    }

    [Fact]
    public void TheMediaKeysHalfOfAKeyboardIsNotAKeyboard()
    {
        // Esc and Enter and a pile of consumer codes, but no letters: the hotkey and the shortcut
        // that aborts it both live on the other node.
        Assert.Empty(InputDeviceCatalog.Parse(Catalog(ConsumerControl)));
    }

    [Fact]
    public void ARemappersVirtualKeyboardCounts()
    {
        // keyd, kmonad, an evremap - they grab the physical keyboard and re-emit through a virtual
        // one. Skipping it would mean the hotkey does nothing on exactly the machines whose owners
        // are most likely to have moved their keys around.
        KeyboardDevice only = Assert.Single(InputDeviceCatalog.Parse(Catalog(VirtualKeyboard)));
        Assert.Equal("event17", only.EventNode);
    }

    [Fact]
    public void EveryKeyboardInTheFileIsFoundAndNothingElseIs()
    {
        IReadOnlyList<KeyboardDevice> keyboards = InputDeviceCatalog.Parse(
            Catalog(PowerButton, UsbKeyboard, ConsumerControl, Mouse, VirtualKeyboard));

        Assert.Equal(["event2", "event17"], keyboards.Select(device => device.EventNode));
    }

    [Fact]
    public void TheLastBlockCountsEvenWithoutATrailingBlankLine()
    {
        string catalog = string.Join("\n\n", [PowerButton, UsbKeyboard]);

        Assert.Single(InputDeviceCatalog.Parse(catalog));
    }

    [Fact]
    public void ADeviceWithNoEventNodeIsSkipped()
    {
        // The kbd handler is the kernel's own console consumer; there is nothing to open.
        string catalog = Catalog(UsbKeyboard.Replace(
            "H: Handlers=sysrq kbd leds event2",
            "H: Handlers=sysrq kbd leds",
            StringComparison.Ordinal));

        Assert.Empty(InputDeviceCatalog.Parse(catalog));
    }

    [Fact]
    public void ADeviceThatDoesNotReportKeysAtAllIsSkipped()
    {
        // EV without the EV_KEY bit. A touchpad in absolute-only mode, a rotary knob, an
        // accelerometer.
        string catalog = Catalog(UsbKeyboard.Replace("B: EV=120013", "B: EV=9", StringComparison.Ordinal));

        Assert.Empty(InputDeviceCatalog.Parse(catalog));
    }

    [Fact]
    public void NonsenseIsNotADevice()
    {
        // The file grows new line kinds between kernel versions, and a parser that guessed would
        // open something that is not a keyboard.
        Assert.Empty(InputDeviceCatalog.Parse(string.Empty));
        Assert.Empty(InputDeviceCatalog.Parse("N: Name=\"Half a block\"\nH: Handlers=event9\n"));
        Assert.Empty(InputDeviceCatalog.Parse(Catalog(
            UsbKeyboard.Replace("B: KEY=1000000000007", "B: KEY=zzzz", StringComparison.Ordinal)
                       .Replace(" fffffffffffffffe", " nonsense", StringComparison.Ordinal))));
    }

    [Fact]
    public void TheKernelsOwnListIsWhereThisReadsFrom()
    {
        // Named rather than assumed, because the whole reason for parsing text instead of calling
        // EVIOCGBIT is that this particular file can be read without permission to open a device.
        Assert.Equal("/proc/bus/input/devices", InputDeviceCatalog.DevicesFile);
        Assert.Equal("/dev/input", InputDeviceCatalog.DeviceDirectory);
    }
}
