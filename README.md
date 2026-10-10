# WhiteFox

Reimplementación en **Unity 6 (URP)** del cliente de Minecraft Java Edition **[Minosoft](https://gitlab.bixilon.de/bixilon/minosoft)**
(Kotlin, GPLv3). Versión objetivo: **1.20.4**.

> No está afiliado con Mojang AB. No se distribuye ningún asset de Minecraft: el `client.jar` se descarga de los
> servidores de Mojang en el primer arranque, se verifica por SHA-1 y solo se usan sus texturas y modelos.

## Estado (hito 1: mundo plano offline)

- Descarga y caché de assets (manifest de versiones de Mojang → `client.jar`), con resource packs (zip o carpeta).
- Registro de bloques y estados con los mismos ids globales que 1.20.4 (datos de [minecraft-data](https://github.com/PrismarineJS/minecraft-data), fijados por commit y hash).
- Modelos de bloque: `blockstates` (variants/multipart/weighted), herencia por `parent`, rotaciones de elemento y de estado, `uvlock`.
- `Texture2DArray` con todas las texturas de bloque y texturas animadas (`.mcmeta`).
- Mesher por sección en hilos de trabajo: culling de caras vecinas, AO por vértice, luz por cara, tinte de bioma, sombreado por dirección, opaco/cutout/traslúcido.
- Motor de luz propio (cielo + bloque), recálculo local al cambiar bloques.
- Física vanilla del jugador a 20 TPS: gravedad, fricción, salto, sprint, agacharse (no caer de bordes), subir escalones, vuelo creativo (doble espacio).
- Romper / colocar / elegir bloque (creativo), con orientación básica (ejes, `facing`, losas y escaleras arriba/abajo).
- Ciclo día/noche, niebla, HUD (crosshair, hotbar, pantalla F3).

## Conexión a servidores (1.20.4, protocolo 765)

WhiteFox es un cliente: se conecta a servidores como Minosoft. El menú principal permite hacer ping (MOTD,
jugadores, latencia) y conectar con una cuenta offline. Implementado:

- Handshake → login → configuración → play, compresión zlib y cifrado AES/CFB8 (preparado; el login online con
  cuenta Microsoft aún no, así que de momento solo servidores en `online-mode=false`).
- Registros del servidor (tipos de dimensión: altura del mundo), chunks con paletas, cambios de bloque individuales
  y por sección, descarga de chunks, lotes de chunks, keep-alive y ping.
- Posición sincronizada con confirmación de teleport; envío de movimiento como Minosoft (posición/rotación solo si
  cambian, suelo, sprint, agacharse, vuelo).
- Chat (T o /) y comandos sin firmar, mensajes de sistema/jugador/action bar con traducciones en_us.
- Romper (creativo instantáneo, supervivencia con progreso a mano), colocar (Use Item On), hotbar con el inventario
  real, vida/hambre y reaparición automática, modo de juego y habilidades, hora del mundo, cambio de dimensión.
- Todo lo que llega se decodifica en el hilo de red y se aplica en orden en el hilo del juego con un presupuesto de
  tiempo por frame.

Servidor de pruebas local: `Tools/TestServer/start-test-server.ps1` (descarga el server.jar oficial, offline, solo
127.0.0.1). Prueba automática: `cd Tools/CoreTests; dotnet run -- ./cache --server 127.0.0.1:25565`.
Directo desde la línea de comandos: `WhiteFox.exe -wf-connect host:puerto -wf-name Nombre`.

## Cuentas y configuración

- **Cuentas Microsoft (opcionales)**: Cuentas... en la pantalla de inicio. Se inicia sesión con un código (se abre
  microsoft.com/link y el código queda copiado), como los launchers. Después no hay que volver a entrar: se guarda el
  token de renovación (dura meses y se renueva solo al abrir el cliente) y la sesión de Minecraft de 24 h se renueva en
  silencio antes de conectar. Con una cuenta Microsoft se entra a servidores en modo online (autenticación con Mojang
  y cifrado AES/CFB8); sin cuenta se usa el nombre offline como siempre. Varias cuentas, una seleccionada.
- Los tokens se guardan cifrados para el usuario de Windows (DPAPI) en `accounts.toml`; ese archivo no sirve en otro
  equipo ni en otro usuario.
- **Toda la configuración** (gráficos, calidad, controles, volúmenes, paquetes de recursos, último servidor y nombre
  offline) va en `config.toml`, legible y editable a mano, en `Application.persistentDataPath`
  (`%USERPROFILE%\AppData\LocalLow\WhiteFox\WhiteFox`). Se guarda al cambiar opciones y al cerrar; la primera vez
  se importa lo que había en PlayerPrefs.
- Para los inicios de sesión hace falta un **client id de Azure** en `config.toml` (`[microsoft] client_id`): una
  app registrada en portal.azure.com para cuentas personales con "flujos de cliente públicos" activados. Mojang
  además exige pedir acceso a su API de inicio de sesión para apps nuevas; hasta entonces responde 403 y el cliente
  lo dice.

## Interfaz y opciones

La interfaz usa los recursos de interfaz del propio juego (sprites de `textures/gui/sprites`, fuente bitmap de
`font/include/default.json`, panorama del título del índice de assets): título con panorama giratorio y splash,
botones/campos/sliders con nine-slice, hotbar con iconos 3D de bloques, corazones, comida, chat, action bar, F3,
menú de pausa (Esc) y pantallas de conexión/desconexión.

**Opciones** (título o pausa), guardadas en PlayerPrefs:
- Gráficos **Clásicos** (el aspecto de Minosoft, solo cambia la distancia de renderizado) o **Modernos**.
- Distancia de renderizado, campo de visión, sensibilidad.
- **Controles**: todas las teclas se pueden reasignar (clic y pulsar la nueva tecla; Esc cancela), con aviso de
  conflictos y reinicio individual o total.
- **Calidad gráfica** (solo Modernos), con presets Bajo/Medio/Alto/Ultra: sombras del sol, cantidad de luces reales,
  luces con sombra, relieve (normal maps), agua simple/realista, bloom y postprocesado.

## Jugador

- Física vanilla: caminar, correr (Ctrl o doble W), agacharse sin caer de bordes, saltar, escalones, vuelo creativo,
  **nadar** (agua y lava con su fricción, flotar, hundirse con Shift, nado rápido con pose tumbada, corrientes),
  **escaleras y enredaderas**, telarañas/arbustos/nieve polvo, hielo/slime/arena de almas/miel, efectos de
  estado (velocidad, lentitud, salto, levitación, caída lenta, gracia de delfín, ceguera) y hambre (sin correr con ≤ 6).
- Minado de supervivencia con la velocidad real de cada herramienta, eficiencia, prisa/fatiga, bajo el agua y en el
  aire, y las grietas sobre el bloque.
- Inventario (E): supervivencia (armadura, crafteo 2×2, mano secundaria), creativo con búsqueda de todos los ítems,
  cofres y demás contenedores del servidor, con clics predichos y shift-clic.
- Soltar ítem (Q, Ctrl+Q la pila), cambiar de mano (F), usar ítems (comer, arco, escudo, pociones...), con migas
  del ítem y sonido al comer o beber (también en otros jugadores) y trozos del ítem al romperse una herramienta.
- Élitra (saltar en el aire para planear, física vanilla de planeo, cohetes que impulsan), tridente con propulsión
  (bajo el agua o la lluvia), cerdos y striders guiados con la caña, salto cargado del caballo con su barra.
- HUD: vida, comida, armadura, aire, barra y nivel de experiencia, efectos activos, mano secundaria; niebla bajo el
  agua/lava, visión nocturna y pantalla de muerte con reaparecer.

## Entidades

- Modelos vanilla con animaciones (caminar, mirar, golpear, agacharse, montar, nadar, muerte, parpadeo rojo al recibir
  daño): jugadores (skin por defecto según UUID como vanilla, o la del perfil en modo online; skins 64×32 convertidas),
  zombis/husk/drowned, esqueletos, creeper, araña, vaca/champiñaca, cerdo (con montura), oveja (color y esquilado),
  gallina, enderman, aldeanos/brujas/illagers, slime, lobo, gato/ocelote, golems de hierro y nieve, piglins (y
  zombificados, brutos), llama, abeja, peces (bacalao, salmón, tropical, pez globo), calamar, murciélago, zorro, oso
  polar, blaze, ghast, caballos/burros/mulas (con montura), tortuga, delfín, axolotl, rana, cabra, conejo, loro,
  camello (montable), sniffer, warden, guardianes, wither, dragón, soportes de armadura (pose, brazos, base), botes
  (cada madera) y vagonetas. Lo que aún no tiene modelo se dibuja como su caja de colisión.
- Variantes de textura: gatos, colores y manchas de caballo, llamas, loros, conejos, zorros, axolotls, ranas,
  champiñacas, peces tropicales (forma, patrón y colores), aldeanos y aldeanos zombi (bioma, profesión y nivel).
- Dragón con alas que baten, mandíbula, cuello y cola que se mecen; wither con costillas y cola que respiran y
  cabezas que miran; guardianes con sus 12 púas que se recogen al nadar.
- Brillo de encantamiento (textura y desplazamiento vanilla) en ítems encantados, libros, pociones, manzanas de
  Notch... en el inventario, la mano, el suelo y la armadura puesta.
- Armadura puesta en jugadores, mobs humanoides y soportes (cuero teñido incluido), también en la vista F5 propia.
- Ítems soltados, en la mano y en primera persona con grosor (cada píxel del borde extruido, como vanilla), orbes de
  experiencia, bloques que caen, flechas, proyectiles lanzados (bolas de nieve, huevos, perlas...), marcos (y marcos
  brillantes) con su ítem girado y cuadros (las 30 variantes).
- Atacar (clic izquierdo) e interactuar/montar (clic derecho); los pasajeros se colocan en el cliente como en vanilla.
  Los caballos ensillados se conducen desde el cliente (WASD, saltar, suben un bloque), como los botes.

## Mundo y ambiente

- Colores por bioma (registro de biomas del servidor y biomas de cada chunk): pasto, hojas y agua del color de su
  bioma, mezclados en las fronteras; cielo, niebla y niebla bajo el agua del bioma. Nether con su niebla, End con su
  cielo oscuro.
- Partículas del servidor (las ~100 del registro con sus sprites), al romper y picar bloques, y las de ambiente que
  vanilla genera en el cliente (antorchas, fuego, lava, portales, fogatas, hornos, velas...).
- Clima: lluvia y nieve según el bioma y la altura, salpicaduras y sonido de lluvia, cielo gris; rayos con destello.
- Nubes vanilla (fancy) a la altura 192, moviéndose hacia el este.
- Barras de jefe con sus flags: el wither oscurece el cielo poco a poco, el dragón trae la niebla espesa.
- Luces dinámicas (mejora tipo OptiFine): antorcha, linterna, glowstone... en la mano propia o de otros jugadores y
  mobs, ítems luminosos en el suelo y entidades en llamas iluminan su alrededor (Moderno: luz real; Clásico: mapa de
  luz).

## Interfaz del servidor

- Títulos y subtítulos, barra de acción, barras de jefe (colores y divisiones), marcador lateral (equipos, prefijos,
  formatos de número), lista de jugadores con encabezado/pie y puntuación, puntuación bajo el nombre, avisos de
  logros.
- Chat con colores, hex, negrita, subrayado, tachado, ofuscado; enlaces clicables (con confirmación), comandos,
  sugerencias, copiar, y texto al pasar el ratón.
- Pantallas de contenedores: mesa de crafteo, dispensador/soltador, hornos (con llama y flecha de progreso),
  destilador, yunque (renombrar), mesa de encantamientos (costes), comercio con aldeanos, afiladora, mesa de herrería,
  telar, cortapiedras, mesa de cartografía, faro (efectos), crafter, tolva, cofres, y el libro de recetas.
- Editor de carteles, libros (leer libros escritos, escribir y firmar libros y plumas) y mapas con sus marcadores
  (jugadores, marcos, estandartes, estructuras), en marcos, en una mano o con las dos manos como vanilla.
- Inventario: el muñeco del jugador sigue al ratón; iconos de cofres, camas, estandartes y escudos (con sus
  patrones), cabezas y cajas de shulker con la transformación `gui` de su modelo; modelos de inventario propios
  (vallas, muros, botones...).

## Bloques con contenido (block entities)

- Cofres (simples, dobles, trampa, de ender) con la tapa que se abre cuando alguien los usa (Block Action), carteles de
  pie, de pared y colgantes con su texto (colores, texto brillante, ambas caras), camas, estandartes con sus patrones
  (compuestos en una textura) y ondeando, cabezas (esqueleto, wither, zombi, creeper, piglin, jugador con su skin) y
  cajas de shulker que se abren. Los datos llegan con el chunk y con Block Entity Data; el cave culling también los
  oculta.

## Sonido

- Todos los sonidos del juego: los que manda el servidor (Sound Effect, Entity Sound Effect, roturas de otros jugadores)
  y los que vanilla reproduce en el propio cliente (pasos, golpes al picar, romper y colocar, abrir puertas,
  trampillas y vallas, daño y muerte de entidades, clic de botones). Audio 3D con la atenuación lineal de vanilla.
- Música como vanilla según la situación: menú, juego, creativo, la de cada bioma (Nether incluido), bajo el agua en
  océanos y ríos, End y la pelea con el dragón, con las pausas de cada una; ambiente de cueva en la oscuridad.
- Tocadiscos: el disco suena desde el bloque (se oye a 64 bloques), "Reproduciendo" en la barra de acción, y la
  música se pausa mientras tanto. Bloques de notas con su nota de color.
- Sonido de cada bloque según su tipo vanilla (maderas de cerezo, bambú y del Nether, carteles colgantes, bloques
  del Nether, cultivos, corales...).
- Los .ogg se descargan del índice de assets de Mojang la primera vez que suenan (verificados por SHA-1 y guardados en
  la caché); un paquete de recursos puede reemplazarlos. Volumen por categoría en Opciones → Música y sonidos.

## Paquetes de recursos

- Opciones → Paquetes de recursos: disponibles y seleccionados (el de arriba gana), abrir la carpeta; al aceptar se
  recargan modelos, texturas, fuentes, sonidos y se vuelve a mallar el mundo sin desconectarse.
- Paquetes del servidor (Add/Remove Resource Pack, en configuración y en juego): pregunta como vanilla (o siempre
  sí/no), descarga y verifica el SHA-1, responde los estados (aceptado, descargado, cargado...) y los quita al salir.
  Si el paquete es obligatorio y se rechaza, se desconecta.
- Ítem en la mano de jugadores y mobs, nombres sobre jugadores y mobs con nombre, lista de jugadores (Tab).
- Primera persona: brazo con la skin propia o el ítem en la mano, con animación de golpe, de cambio de ítem y
  balanceo de la cámara al caminar. F5: tercera persona detrás/delante, la cámara se acerca si choca con bloques.
- Clásico: iluminación de entidades vanilla; Moderno: sol con sombras proyectadas por las entidades, cielo ambiente y
  luces puntuales.

## Modo realista (F6)

Activado por defecto; F6 alterna con el modo clásico (el aspecto de Minosoft/vanilla). `-wf-classic` arranca en clásico.

- **Sol y luna** como luz direccional con sombras en cascada que siguen la hora del día; cielo procedural con
  sol, luna y estrellas; tonemapping ACES, bloom y corrección de color.
- **Luces reales:** cada bloque que emite luz (antorchas, glowstone, lava, linternas…) cercano es una luz puntual
  con atenuación física y color propio; las más cercanas proyectan sombras. Los niveles de luz de bloque de
  Minecraft no se usan en este modo (solo la visibilidad del cielo, suavizada por vértice, como oclusión ambiental).
- **Materiales PBR** compatibles con resource packs **LabPBR** (`*_n.png`, `*_s.png`): normal map, AO, suavidad,
  F0/metal y emisión. Sin pack PBR se generan normales suaves a partir del albedo.
- **Agua:** olas animadas, reflejo Fresnel del cielo, brillo del sol, refracción y absorción por canal según la
  profundidad. Lava emisiva con bloom.

Los shaderpacks de OptiFine/Iris (GLSL) no son compatibles: Unity no compila shaders en tiempo de ejecución y su
pipeline es distinto; sí lo son sus resource packs LabPBR.

## Uso

1. Abre la carpeta del proyecto con **Unity 6000.0.5f1** (o un Unity 6 compatible).
2. Abre `Assets/Scenes/SampleScene` y pulsa Play (el cliente también arranca solo en cualquier escena vacía).
3. El primer arranque descarga unos 25 MB a `Application.persistentDataPath/cache`.
4. Resource packs: copia zips o carpetas a `Application.persistentDataPath/resourcepacks`.

| Control | Acción |
|---|---|
| WASD / ratón | Moverse / mirar |
| Espacio (x2) | Saltar (doble pulsación: volar) |
| Shift / Ctrl | Agacharse o bajar volando / sprint |
| Clic izq. / der. / central | Romper / colocar / elegir bloque |
| 1–9, rueda | Ranura de la hotbar |
| F3 | Pantalla de depuración |
| F5 | Primera / tercera persona |
| F6 | Gráficos modernos / clásicos |
| Tab | Lista de jugadores |
| T, / | Chat / comando |
| Esc | Liberar el ratón |

Build de Windows: menú **WhiteFox → Build Windows player** (o `-executeMethod WhiteFox.Editor.WhiteFoxBuild.BuildWindows`).
Captura automática para pruebas visuales:
`WhiteFox.exe -wf-screenshot shot.png -wf-pose <x> <y> <z> <yaw> <pitch> [-wf-time <ticks>]`; con un servidor:
`-wf-connect host -wf-chat "/cmd;/cmd" [-wf-inventory] [-wf-use x,y,z] [-wf-useitem] [-wf-holduse]` (si el jugador
de pruebas está muerto, reaparece primero).

### Tests del núcleo

El núcleo no depende de Unity y se compila también con .NET (C# 9, igual que Unity):

```
cd Tools/CoreTests
dotnet run -- ./cache
```

Comprueba ids de estados, modelos horneados, texturas, luz, mallado, la física (velocidad al caminar/esprintar,
altura de salto 1.2522, escalones) y la interacción.

## Arquitectura

```
Assets/WhiteFox/
  Core/            sin referencias a UnityEngine (asmdef noEngineReferences)
    Assets/        AssetManager por capas, descargas verificadas      ← Minosoft assets/
    Data/          Block, BlockState, BlockRegistry                     ← data/registries/blocks
    Worlds/        World, Chunk, ChunkSection, LightEngine              ← data/world
    Models/        BlockModel, BlockStateDefinition, ModelBaker         ← gui/rendering/models
    Textures/      TextureManager (array + animaciones)                 ← rendering/system/base/texture
    Meshing/       SectionMesher, ChunkVertex                           ← rendering/chunk/mesher
    Physics/       LocalPlayer, PlayerPhysics                           ← physics/
    Game/          GameSession (tick 20 TPS), Raycast, Placement        ← PlaySession, camera/target, input/interaction
    Local/         FlatGenerator, LocalChunkManager                     ← local/
  Client/          capa Unity: WhiteFoxClient, WorldRenderer, BlockTextureArray, input, HUD
  Resources/WhiteFox/Block.shader   shader de chunks (URP + fallback built-in)
Tools/CoreTests/   proyecto .NET que compila Core y ejecuta pruebas
```

Convenciones:
- Coordenadas internas = Minecraft (diestro, +X este, +Y arriba, +Z sur). Al renderizar se niega Z (Unity es zurdo).
- Yaw 0 = sur, crece girando a la derecha; pitch positivo = mirar abajo. Cámara: `Quaternion.Euler(pitch, yaw + 180, 0)`.
- Las mutaciones del mundo ocurren solo en el hilo del juego; el mesher trabaja sobre una copia 18³ de la sección.

Diferencias deliberadas con Minosoft:
- PixLyzer se sustituye por minecraft-data (JSON público, mismos ids de estado).
- Un único `Texture2DArray` (las texturas pequeñas se escalan) en lugar de un array por resolución.
- Animaciones copiadas en GPU (`Graphics.CopyTexture`) en lugar de re-subir los píxeles.
- Luz: cola iterativa (sin recursión) y recálculo de la región 3×3 de chunks al cambiar un bloque.
- Colisión: comparación del orden de ejes con valor absoluto (como vanilla).
- Frustum culling lo hace Unity (un GameObject por sección).
- Cave culling como Minosoft/vanilla: cada sección guarda qué caras se ven entre sí a través de bloques no opacos
  (grafo 6×6 calculado al mallar) y un recorrido en anchura desde la sección de la cámara, en un hilo de trabajo,
  oculta lo que no se alcanza. Las secciones ocultas siguen proyectando sombra del sol (una cueva no se ilumina).
  `-wf-nocull` lo desactiva para comparar; F3 muestra secciones visibles/total.
- Origen flotante: todo se dibuja relativo a un origen cercano a la cámara (múltiplo de 16, se mueve al alejarse
  1024 bloques), la física sigue en doubles. A 20 millones de bloques el mundo se ve igual que en el spawn
  (`-wf-noorigin` muestra cómo se rompe sin él).

## Próximos pasos

1. Lista de servidores guardada (con ping, MOTD e icono). Más adelante: versión web (WebGL, con un proxy
   WebSocket→TCP y un backend para el login) y controles táctiles estilo MCPE.
2. Aproximaciones que quedan: el cuello y la cola del dragón no siguen su trayectoria (vanilla usa el historial de
   posiciones), las cabezas laterales del wither no miran a sus objetivos, la fuerza de salto del caballo es la media
   (el atributo no llega al cliente), y el patrón de estandartes/escudos en la mano usa el ítem sin patrón.

## Licencia

GPL-3.0-or-later (ver `LICENSE.md`). Es un trabajo derivado de Minosoft, Copyright (C) 2020-2023 Moritz Zwerger.
Los datos de bloques provienen de minecraft-data (MIT) y se descargan en tiempo de ejecución.
