// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

namespace EditFlow.Engine;

/// <summary>Rutas a los ejecutables de FFmpeg que usa EditFlow.</summary>
/// <param name="FFmpegPath">Ruta completa a ffmpeg.</param>
/// <param name="FFprobePath">Ruta completa a ffprobe.</param>
/// <param name="Origin">De dónde se resolvieron, para mostrarlo en diagnósticos.</param>
public sealed record FFmpegTools(string FFmpegPath, string FFprobePath, string Origin);
