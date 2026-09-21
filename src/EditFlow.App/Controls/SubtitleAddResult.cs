// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

using System;

namespace EditFlow.App.Controls;

/// <summary>Resultado de añadir una tanda de subtítulos.</summary>
/// <param name="Added">Subtítulos colocados.</param>
/// <param name="Skipped">Los que no cupieron porque ya había otro en ese instante.</param>
/// <param name="FirstStart">Dónde empieza el primero, si se colocó alguno.</param>
public readonly record struct SubtitleAddResult(int Added, int Skipped, TimeSpan? FirstStart);
