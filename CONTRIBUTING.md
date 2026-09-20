# Guía de contribución — EditFlow

## Configuración inicial

Tras clonar el repositorio, **activa los hooks**:

```bash
git config core.hooksPath .githooks
```

No es opcional: los hooks validan el formato de los commits y son la última barrera automática contra la filtración de datos sensibles.

---

## 🔒 Regla inquebrantable: nada sensible llega al repositorio

Esta regla tiene prioridad sobre cualquier otra. **Ante la duda, no se commitea.**

El historial de git es **permanente**. Borrar un archivo en un commit posterior no lo elimina del historial: sigue siendo recuperable por cualquiera que clone el repositorio. Por eso la barrera está *antes* del commit, no después.

### Qué no entra nunca

| Categoría | Ejemplos |
|---|---|
| Credenciales | Tokens, claves de API, certificados (`.pfx`, `.p12`, `.snk`, `.key`, `.pem`), archivos `.env` |
| Rutas absolutas de usuario | Cualquier ruta que contenga el nombre de la cuenta del sistema operativo. Usa `Environment.GetFolderPath()` o rutas relativas |
| Medios | Videos y audio de prueba. Los tests **generan** sus propios clips con `ffmpeg -f lavfi -i testsrc` |
| Proyectos `.editflow` | Contienen rutas absolutas a los videos del usuario |
| Cachés y logs | `cache/`, `thumbnails/`, `logs/`, volcados de fallo |
| Binarios de FFmpeg | Van en `tools/ffmpeg/`, que está ignorado. Se obtienen con `tools/fetch-ffmpeg.ps1` |

### Las tres barreras

1. **`.gitignore` endurecido** — cubre todas las categorías anteriores.
2. **Hook `pre-commit`** — bloquea el commit si detecta archivos de medios (incluso forzados con `git add -f`), secretos, archivos de más de 10 MB, rutas absolutas de usuario o patrones de credenciales conocidos.
3. **Revisión manual** — ejecuta `git status` y `git diff --cached` antes de cada push relevante.

### Si algo sensible llega a entrar

No basta con borrarlo en un commit nuevo. Hay que:

1. Reescribir el historial con [`git filter-repo`](https://github.com/newren/git-filter-repo).
2. Forzar el push de las ramas afectadas.
3. **Rotar toda credencial expuesta** — asume que ya fue comprometida.

---

## Flujo de ramas — Git Flow

| Rama | Nace de | Se integra en | Propósito |
|---|---|---|---|
| `main` | — | — | Solo releases. Cada merge lleva un tag SemVer. **Nunca se commitea directo.** |
| `develop` | `main` | — | Rama de integración. Base de todo el desarrollo. |
| `feature/*` | `develop` | `develop` | Una funcionalidad. Ej: `feature/encoder-detection` |
| `release/*` | `develop` | `main` + `develop` | Estabilización de una versión. Ej: `release/0.1.0` |
| `hotfix/*` | `main` | `main` + `develop` | Corrección urgente sobre producción. Ej: `hotfix/0.1.1` |

```
main     ──o───────────────────o──────────────o──►   v0.1.0    v0.2.0
            \                 /              /
develop  ────o───o───o───o───o───o───o───o──o──►
                  \ /         \ /     \ /
        feature/engine  feature/timeline  feature/audio-track
```

- Merges a `develop`: **squash**, para que cada funcionalidad quede como un commit limpio.
- Merges de `release` y `hotfix` a `main`: **`--no-ff`**, para que el punto de release quede visible en el grafo.

### Trabajar una funcionalidad

```bash
git checkout develop && git pull
git checkout -b feature/mi-funcionalidad

# ... desarrollo, con commits conformes ...

dotnet test                  # debe pasar antes de abrir el PR
git push -u origin feature/mi-funcionalidad
```

---

## Mensajes de commit — Conventional Commits 1.0.0

```
<type>(<scope>)<!>: <description>
```

La descripción va en **inglés**, en modo imperativo, con un máximo de **72 caracteres** y **sin punto final**.

**Types**

| Type | Cuándo |
|---|---|
| `feat` | Nueva funcionalidad |
| `fix` | Corrección de un error |
| `docs` | Solo documentación |
| `style` | Formato, sin cambio de comportamiento |
| `refactor` | Reestructuración sin cambiar comportamiento |
| `perf` | Mejora de rendimiento |
| `test` | Añadir o corregir tests |
| `build` | Sistema de compilación o dependencias |
| `ci` | Integración continua |
| `chore` | Mantenimiento varios |
| `revert` | Revertir un commit anterior |

**Scopes**: `core` · `engine` · `app` · `timeline` · `export` · `preview` · `ffmpeg` · `deps`

**Ejemplos**

```
feat(engine): detect NVENC/QSV/AMF encoders with smoke test
feat(timeline): split clip at playhead with S key
fix(export): force -b:v 0 in NVENC CQ mode
perf(timeline): cache clip thumbnails on disk
test(engine): cover filter graph for clip without audio
build(ffmpeg): add download script with SHA-256 verification
chore(deps): pin Avalonia to [11.3.13,12.0.0)
```

**Cambios incompatibles** — `!` antes de los dos puntos y un footer explicativo:

```
feat(core)!: change project file schema

BREAKING CHANGE: projects saved with v0.1.x must be re-created.
```

El hook `commit-msg` rechaza automáticamente cualquier mensaje fuera de norma.

---

## Versionado — SemVer 2.0.0

Tags anotados: `v0.1.0`, `v0.2.0`, `v1.0.0`.

| Incremento | Cuándo |
|---|---|
| `MAJOR` | Cambio incompatible |
| `MINOR` | Nueva funcionalidad retrocompatible |
| `PATCH` | Corrección retrocompatible |

Cada release actualiza `CHANGELOG.md` siguiendo [Keep a Changelog](https://keepachangelog.com/), agrupando en `Added` / `Changed` / `Fixed` / `Removed`.

---

## Arquitectura: la regla de oro

**`EditFlow.Core` y `EditFlow.Engine` no referencian Avalonia.**

Todo el modelo de datos y toda la lógica de construcción de comandos FFmpeg deben ser testeables desde consola, sin entorno gráfico. Es lo que permite que la suite de tests corra en CI sobre Linux.

Si necesitas tocar la interfaz desde el motor, la dependencia va al revés: define una interfaz en `Core` o `Engine` e impleméntala en `App`.

## Tests

```bash
dotnet test
```

Toda corrección de un error debería venir acompañada de un test que falle sin el arreglo. Los tests **no dependen de FFmpeg ni de la interfaz**: verifican los argumentos generados contra snapshots, y parsean fixtures con salidas reales capturadas previamente.
