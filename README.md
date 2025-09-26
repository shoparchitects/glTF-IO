# glTF-IO

Rhinocerous plugin to export Rhino objects to [glTF Binary](https://www.khronos.org/gltf/) for use on the web/mobile/VR/etc.

Rhino users with questions should go here: https://discourse.mcneel.com/t/gltf-binexport/114378

Bug reports are welcome here.

# For Developers
Everything you need to know about glTF is available on this poster: https://github.com/KhronosGroup/glTF/blob/master/specification/2.0/figures/gltfOverview-2.0.0b.png

Contributions and bug reports are welcome.

## Build Releases and Deploying
This project uses the Rhino7 and above `PackageManger` for managing the user releases.
Building and deploying for both Win and Mac can be done with: (cd into the directory where you have the manifest.yml)
You need to build separately the .rhps and .dlls for .net 4.8 (Rhino 7) in folder net4.8 and .net 7 (Rhino 8) in folder net7
Currently I am experiencing some hiccups with Rhino 7 enabling the plugin automatically after install so you would need to copy and paste the same files directly outside the net4.8 folder as well (duplicates)
Make sure to rename the .yak file to any (instead of rh7.xx) to target both Rhino 7 and 8 after build and before push
It's a bit of a hack but it works
```
$ "C:\Program Files\Rhino 7\System\Yak.exe" build
$ "C:\Program Files\Rhino 7\System\Yak.exe" push glTF-IO-extension-by-SHoP-XXXXXXX-any.yak
```

# Sponsors
[Stykka ApS](https://stykka.com)

[McNeel](https://rhino3d.com)

# Contributors
[Aske Doerge](https://github.com/Doerge) (original author)

[Joshua Kennedy](https://github.com/jrz371)

[Peter Krattenmacher](https://github.com/pkratten)

[Ali Tehami](https://github.com/alitehami)

[Tim Li](https://github.com/timera)

# License
MIT but please make PRs if you make improvements.

