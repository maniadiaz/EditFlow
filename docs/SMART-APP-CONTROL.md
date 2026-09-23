<!--
SPDX-FileCopyrightText: 2026 maniadiaz
SPDX-License-Identifier: GPL-3.0-or-later
-->

# Si EditFlow no abre: Smart App Control

Este documento existe porque el síntoma es confuso y el mensaje que da Windows
apunta al sitio equivocado. Todo lo que hay aquí está comprobado en un equipo real,
no deducido.

## El síntoma

Al abrir EditFlow recién instalado aparece un cuadro de diálogo parecido a este:

> `%LOCALAPPDATA%\Programs\EditFlow\ffmpeg\avformat-63.dll` no está
> diseñado para ejecutarse en Windows o contiene un error. Intente instalar el programa
> de nuevo… **Estado del error 0xc0e90002.**

El mensaje **miente sin querer**: el archivo no está corrupto ni mal compilado.
Reinstalar no arregla nada, y volver a descargarlo tampoco.

`0xC0E90002` significa *"el Control de aplicaciones no dejó cargar esto"*.

### La otra variante: ni siquiera arranca el instalador

A veces el bloqueo llega antes, y al ejecutar el `.exe` sale esto:

> `EditFlow-0.6.3-win-x64-setup.exe` ha sido bloqueado por la directiva de **Device Guard**
> de su organización. Póngase en contacto con el personal de soporte técnico.

Es el mismo Smart App Control, con otro nombre y otro mensaje. Y tampoco es que tu
organización haya configurado nada: si el equipo es tuyo, no hay ninguna organización
de por medio. Se resuelve igual que lo demás de este documento.

Que un instalador pase y otro no depende de si Microsoft ha visto antes ese archivo
exacto, así que un `.exe` recién compilado tiene más papeletas que uno descargado de una
release que ya lleva tiempo publicada.

## Compruébalo en diez segundos

```bat
tools\check-ffmpeg.cmd
```

Si EditFlow está instalado y no tienes el código fuente, el mismo script viaja dentro
de la instalación:

```bat
"%LOCALAPPDATA%\Programs\EditFlow\tools\check-ffmpeg.cmd"
```

El script ejecuta FFmpeg de verdad, hace una codificación real, y si falla busca la
causa en el registro de eventos de Windows. Termina con:

| Código | Significado |
|---|---|
| 0 | FFmpeg funciona |
| 2 | No se encontró FFmpeg |
| 3 | Smart App Control lo está bloqueando |
| 4 | El binario está roto de verdad |

También puedes verlo con tus propios ojos:

```powershell
Get-WinEvent -LogName Microsoft-Windows-CodeIntegrity/Operational -MaxEvents 10
```

Los bloqueos salen como eventos **3077** y **3118**, y el texto incluye literalmente
*"Smart App Control Block Details"*.

## Qué es Smart App Control

Una función de Windows 11 que impide ejecutar programas sin firma digital de confianza.
Conviene no confundirla con SmartScreen, porque no se parecen:

| | SmartScreen | Smart App Control |
|---|---|---|
| Qué hace | Avisa | Bloquea |
| ¿Puedes seguir? | Sí, *Más información → Ejecutar de todas formas* | No |
| ¿Lo tiene todo el mundo? | Sí | No: solo se activa solo en instalaciones **limpias** de Windows 11 |

Por eso mucha gente distribuye instaladores sin firmar y nadie se queja: la mayoría de
equipos tienen SmartScreen, que se salta con un clic, y no Smart App Control. Si has
visto aplicaciones sin firma funcionando sin problema, es exactamente esto.

Que un equipo actualizado desde Windows 10 no lo tenga activado es lo normal.

## La reputación no sirve de nada aquí

Un mito extendido dice que el bloqueo desaparece cuando el binario acumula descargas
suficientes. **Está comprobado que no**: en el mismo equipo se probó FFmpeg de
`gyan.dev` versión 9.0.1, con más de 84 000 descargas, y quedó bloqueado igual que la
build de BtbN que trae EditFlow.

Lo único que Smart App Control acepta es una firma de código con un certificado de
confianza. No existe forma de sortearlo desde la aplicación.

## Cómo desactivarlo — y la advertencia que importa

> [!CAUTION]
> **Desactivar Smart App Control es irreversible.**
> Microsoft lo diseñó así a propósito: una vez desactivado, **no se puede volver a
> activar**. La única manera de recuperarlo es reinstalar Windows desde cero.
>
> No existe una lista de excepciones, ni un modo "solo por esta vez", ni una forma de
> desactivarlo mientras usas EditFlow y volver a ponerlo después. Si alguna guía te dice
> lo contrario, está equivocada.

Sabiendo eso, la ruta es:

1. Abre **Seguridad de Windows**.
2. Ve a **Control de aplicaciones y navegador**.
3. Entra en **Control inteligente de aplicaciones**.
4. Marca **Desactivado** y confirma.
5. Reinicia y vuelve a ejecutar `check-ffmpeg.cmd`. Debería terminar en 0.

Si el paso 3 no aparece en tu equipo, enhorabuena: no tienes Smart App Control y tu
problema es otro. Ejecuta `check-ffmpeg.cmd`; si sale con código 4, el binario sí está
dañado y se arregla con `tools\fetch-ffmpeg.cmd -Force`.

## Si prefieres no desactivarlo

Es una postura razonable: es una capa de seguridad de verdad y renunciar a ella para
siempre por un programa no es un intercambio obvio.

En ese caso EditFlow no puede funcionar en ese equipo hasta que el proyecto tenga un
certificado de firma de código. En cualquier otro equipo que no tenga la función
activada, el instalador funciona con normalidad.

## Por qué EditFlow no está firmado (todavía)

Un certificado de firma de código es de pago y anual, y además exige validar una
identidad. Es una decisión que aún no se ha tomado. Mientras tanto:

- el instalador se publica con su **SHA-256** en cada release, para que puedas verificar
  que lo que descargaste es lo que se publicó;
- el código está entero en el repositorio y se puede compilar;
- este documento explica el bloqueo en vez de dejarte con un código de error.

> Ojo: compilar desde el código **no evita el bloqueo**. Smart App Control no bloquea a
> EditFlow, bloquea a FFmpeg, que es un binario de terceros y llega sin firmar tanto si
> instalas como si compilas.
