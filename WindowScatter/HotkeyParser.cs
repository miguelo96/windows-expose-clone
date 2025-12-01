public class HotkeyParser
{
    public int ModifierKeys { get; set; } // Combination of MOD_* flags
    public int VirtualKeyCode { get; set; } // The main key

    public static HotkeyParser Parse(string hotkey)
    {
        var parts = hotkey.Split('+');
        var result = new HotkeyParser();

        foreach (var part in parts)
        {
            var key = part.Trim().ToLower();

            switch (key)
            {
                case "ctrl":
                case "control":
                    result.ModifierKeys |= 0x01; // Flag for Ctrl
                    break;

                case "alt":
                    result.ModifierKeys |= 0x02; // Flag for Alt
                    break;

                case "shift":
                    result.ModifierKeys |= 0x04; // Flag for Shift
                    break;

                case "win":
                case "windows":
                    result.ModifierKeys |= 0x08; // Flag for Win
                    break;

                default:
                    // This is the actual key (like "R", "Tab", "F5")
                    result.VirtualKeyCode = GetVirtualKeyCode(key);
                    break;
            }
        }

        return result;
    }

    private static int GetVirtualKeyCode(string keyName)
    {
        keyName = keyName.ToLower();

        // Letters A-Z
        if (keyName.Length == 1 && char.IsLetter(keyName[0]))
        {
            return char.ToUpper(keyName[0]); // 'A' = 0x41, 'Z' = 0x5A
        }

        // Numbers 0-9
        if (keyName.Length == 1 && char.IsDigit(keyName[0]))
        {
            return 0x30 + (keyName[0] - '0'); // '0' = 0x30, '9' = 0x39
        }

        // Special keys
        switch (keyName)
        {
            case "tab": return 0x09;
            case "enter": return 0x0D;
            case "space": return 0x20;
            case "esc":
            case "escape": return 0x1B;
            case "backspace": return 0x08;
            case "delete": return 0x2E;
            case "insert": return 0x2D;
            case "home": return 0x24;
            case "end": return 0x23;
            case "pageup": return 0x21;
            case "pagedown": return 0x22;
            case "left": return 0x25;
            case "up": return 0x26;
            case "right": return 0x27;
            case "down": return 0x28;

            // Function keys
            case "f1": return 0x70;
            case "f2": return 0x71;
            case "f3": return 0x72;
            case "f4": return 0x73;
            case "f5": return 0x74;
            case "f6": return 0x75;
            case "f7": return 0x76;
            case "f8": return 0x77;
            case "f9": return 0x78;
            case "f10": return 0x79;
            case "f11": return 0x7A;
            case "f12": return 0x7B;

            default:
                return 0; // Unknown key
        }
    }
}