## Qué cambia

<!-- Describe el cambio y, sobre todo, por qué era necesario. -->

## Cómo se probó

<!-- Pasos concretos para reproducir la verificación. Si tocaste el motor de
     exportación, indica con qué codificador y resolución lo comprobaste. -->

## Checklist

- [ ] `dotnet test` pasa en local
- [ ] Los commits siguen [Conventional Commits](../CONTRIBUTING.md#mensajes-de-commit--conventional-commits-10)
- [ ] `CHANGELOG.md` actualizado si el cambio es visible para el usuario
- [ ] Se añadió un test que falla sin este arreglo (si es una corrección)
- [ ] `EditFlow.Core` y `EditFlow.Engine` siguen sin referenciar Avalonia

### 🔒 Datos sensibles

- [ ] Revisé `git diff` completo: sin rutas absolutas de usuario, sin credenciales, sin archivos de medios
- [ ] Ningún archivo nuevo debería estar en `.gitignore`
