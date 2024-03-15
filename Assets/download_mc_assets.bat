if exist Assets\1.16.1\minecraft\ (
    echo Minecraft 1.16.1 assets already downloaded
) else (
    echo Downloading Minecraft 1.16.1 assets...
    mkdir Assets\1.16.1\minecraft\
    mkdir tmp
    Rem Download 1.16.1 client file
    curl.exe --output tmp\client.jar --url https://piston-data.mojang.com/v1/objects/c9abbe8ee4fa490751ca70635340b7cf00db83ff/client.jar
    Rem Extract the following folders from the jar file: assets/minecraft/blockstates assets/minecraft/models/block assets/minecraft/textures/block assets/minecraft/textures/entity
    tar.exe -x -f tmp\client.jar -C tmp assets/minecraft/blockstates assets/minecraft/models/block assets/minecraft/textures/block assets/minecraft/textures/entity
    Rem copy the folders to correct destination
    xcopy /s tmp\assets\minecraft\ Assets\1.16.1\minecraft\
    Rem clean
    rmdir /s /q tmp
    echo Done!
)
