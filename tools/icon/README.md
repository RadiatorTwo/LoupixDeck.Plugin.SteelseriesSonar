# Plugin icon generator

`make_icon.py` draws the plugin icon: three mixer faders with level-lit tracks, matte, night blue.
It follows the Audio plugin icon (same tile, colours, matte caps and red marker), so both audio
plugins read as a family. The icon is original artwork, no third-party source; the SteelSeries and
Sonar logos are deliberately not used or imitated.

```bash
pip install pillow numpy
python tools/icon/make_icon.py tools/icon/out
cp tools/icon/out/icon_256.png icon.png
```

The script writes `icon_{256,128,64,32,16}.png` into the given folder; only the 256 px file is
used, as `icon.png` in the repo root. The `out/` folder is not committed.
