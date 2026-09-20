# Changelog

Todos los cambios relevantes de EditFlow se documentan en este archivo.

El formato sigue [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/)
y el proyecto se adhiere a [Versionado Semántico](https://semver.org/lang/es/).

## [Unreleased]

### Added

- Estructura inicial del repositorio con licencia MIT.
- Plan de implementación y decisiones de arquitectura en `docs/PLAN.md`.
- Convenciones de Git Flow, Conventional Commits y SemVer en `CONTRIBUTING.md`.
- Hook `commit-msg` que valida el formato de los mensajes de commit.
- Hook `pre-commit` que bloquea archivos de medios, secretos, archivos de más
  de 10 MB, rutas absolutas de usuario y patrones de credenciales conocidos.
- `.gitignore` endurecido y `.gitattributes` con normalización de fin de línea.

[Unreleased]: https://github.com/maniadiaz/EditFlow/commits/develop
