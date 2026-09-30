# SwitchMonitor: LG 29WK600 en esta Dell

El cambio a DisplayPort ya fue confirmado visualmente: cambia tanto la imagen como
la entrada mostrada en el menu del monitor. La ruta que lo consigue utiliza el
controlador Intel existente, sin instalar DLL ni controladores adicionales.

Puedes instalar `dist\SwitchMonitor-Setup-1.2.0.exe` sin instalar AutoHotkey aparte.
La version portable esta en `dist\SwitchMonitor-Portable-1.2.0.zip`.
Ambas usan el icono generado desde `monitor-switch.png`.

Para ejecutar el codigo fuente, conserva `switchMonitor.ahk`, `intelLegacyDdc.ahk`,
`amdLgDdc.ahk`, `appPaths.ahk` y `returnSyncState.ahk` en la misma carpeta.
Se requiere AutoHotkey v2 de 64 bits. En la configuracion, guarda y activa tus
asignaciones. Si solo hay un monitor se selecciona automaticamente; si hay varios,
usa la lista de monitores.

Atajos guardados actualmente:

- Ctrl+Alt+1: HDMI 1.
- Ctrl+Alt+2: HDMI 2.
- Ctrl+Alt+3: DisplayPort.
- Ctrl+Alt+M: siguiente puerto marcado como conectado (editable).
- Ctrl+Shift+Alt+M: abrir Settings (editable).

Las casillas indican conexiones elegidas por el usuario; no detectan automaticamente
si el otro equipo esta encendido. El ciclo actual incluye HDMI 1 y DP, y omite HDMI 2.
El boton "Switch to selected" usa el mismo transporte que los atajos.

## Varios monitores y menu (1.2.0)

La bandeja muestra el nombre del monitor si solo hay uno, o una lista de nombres
si hay varios. La seleccion se recuerda al reiniciar. El menu contiene
Settings, Shortcuts, Next input (Ctrl+Alt+M) y About. Se ocultan las acciones predeterminadas
de AutoHotkey. Settings y Shortcuts abren con el monitor seleccionado.

Cada monitor conserva su propio perfil; los cambios hechos en varias pantallas
durante la misma sesion de configuracion se guardan al pulsar Guardar y activar.
Los atajos de todos los perfiles se registran a la vez y se comprueba que no
coincidan entre monitores. Saltar se aplica al monitor seleccionado. Las tres
ventanas del programa utilizan colores oscuros inspirados en VS Code. Los atajos
globales se pueden cambiar en Settings con el boton + o quitar con el boton −.

## Funcionamiento y alcance verificado

Para el identificador de modelo GSM7714 conectado a Intel, el script utiliza Intel
CUI y envia el comando LG F4 con origen DDC 0x50: HDMI 1=0x90, HDMI 2=0x91 y DP=0xD0.
Los numeros 17, 18 y 15 que aparecen en la lista siguen siendo los identificadores
de entrada del perfil; la conversion se realiza al enviar el comando.

La estructura nativa se verifico en el controlador instalado, version 8.15.10.4459.
La implementacion se detiene si encuentra otra version. Otros modelos conservan
la ruta ControlMyMonitor/VCP 60. La seleccion no depende de DISPLAY1: se resuelve
el identificador Intel para la pantalla Windows elegida en cada operacion.

En la prueba inicial, HDMI 1 -> DP funciono. El script principal tambien pudo enviarlo.
El retorno fallo tanto tras diez segundos como tras tres segundos con una sesion
Intel nueva. Las lecturas de brillo tampoco respondieron mientras DP estaba visible.
Un cambio correcto puede
dejar a esta computadora sin acceso DDC al monitor mientras muestra otra entrada.
No se considera ese error un cambio completado ni se reintenta con VCP 60.

El ciclo lee la entrada actual antes de elegir el siguiente puerto. Si no consigue
leerla, avisa y no usa la ultima solicitud como si fuera el estado actual. Los atajos
de una entrada concreta siguen intentando enviar el comando directamente.

El boton "Comprobar conexion" y la opcion "Comprobar conexion de control" en el icono
de la bandeja consultan la entrada sin cambiarla. Detectar el monitor en Windows
no garantiza que su canal de control responda mientras muestra otra computadora.
La lectura mas reciente volvio a funcionar y reporto HDMI 1; no fue necesario
reiniciar el controlador para recuperar el acceso.

## Uso en otra computadora

Copia el instalador a la otra PC y ejecutalo. El paquete incluye AutoHotkey y la
distribucion original completa de ControlMyMonitor. No instala drivers.
En la instalacion, los perfiles y registros se guardan en
`%LOCALAPPDATA%\SwitchMonitor`; en la version portable, en `data` junto al EXE.
El codigo fuente conserva los datos en `data` junto al script.
`switchMonitor.ini` es opcional; los perfiles sin numero de serie dependen de la
identidad de Windows, por lo que puede ser necesario configurar los atajos otra vez.

- Este LG en esta Dell: cambio real a DP comprobado.
- Este LG en AMD: se incluye un transporte experimental ADL2 con origen 0x50,
  seleccion dinamica de adaptador/pantalla y registro de los codigos de error.
  Se comprobo imagen de HDMI 1 al volver desde la ASRock, pero el menu conservaba
  DP. Reenviar HDMI 1 desde la Dell corrigio el menu en una prueba visual.
- Este LG en otros Intel/NVIDIA: informa que el transporte no es compatible;
  no se reenvia el VCP 60 que dio problemas en este monitor.
- Otro monitor: se utiliza ControlMyMonitor/VCP 60. Depende de que el monitor,
  la conexion y el controlador permitan cambiar la entrada mediante DDC/CI.

La perdida de control al pasar a DP se observo en esta combinacion de equipos;
no demuestra que todos los monitores bloqueen comandos desde entradas inactivas.

La prueba detallada en AMD figura en `packaging\LEEME.txt`. El boton
"Exportar diagnostico" recoge informacion sin cambiar entradas. Si el retorno
falla, conserva `monitor-diagnose.txt` y `monitor-switch.log` de esa PC.

## Correccion automatica del menu (1.0.1)

Despues de cambiar desde la Dell a otra entrada, se arma un seguimiento cada dos
segundos. Exige perder la comunicacion y recuperarla con dos lecturas consecutivas
de la entrada original. Revalida la identidad del monitor y reenvia esa misma
entrada una vez. Si vuelve en otro puerto, cancela el seguimiento. Los errores
de lectura durante la espera no llenan el registro.

El reenvio manual y el ciclo automatico completo quedaron confirmados visualmente
por el usuario el 29/09/2026. El registro muestra salida a DP a las 16:46:38 y
reenvio automatico de HDMI 1 a las 16:46:46; el usuario confirmo que el menu del LG
se actualizo solo. Tambien pasaron las pruebas de estado del seguimiento.
La Dell debe mantener el programa abierto. No cubre cambios manuales ni reinicios
del programa mientras muestra el otro equipo. No modifica el transporte AMD.

## Diagnostico por comandos

En PowerShell, desde esta carpeta:

```powershell
& 'C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe' .\switchMonitor.ahk --diagnose
```

Genera `data\monitor-diagnose.txt`, sin cambiar entradas. `data\monitor-switch.log` registra
el metodo, la entrada solicitada y los errores reales de Intel. Que Intel acepte
un envio no constituye por si solo confirmacion visual del cambio.

Para activar inmediatamente las asignaciones guardadas si hay un solo monitor:

```powershell
& 'C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe' .\switchMonitor.ahk --activate
```

Pruebas sin escrituras al monitor: `switchMonitor.ahk --self-test` e
`intel-ddc-selftest.ahk`.
