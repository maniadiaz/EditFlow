// SPDX-FileCopyrightText: 2026 maniadiaz
// SPDX-License-Identifier: GPL-3.0-or-later

// Las pruebas de integración miden tiempos reales de reproducción y de FFmpeg. Ejecutadas a la
// vez, compiten por la CPU y una espera de 400 ms deja de bastar para recibir un fotograma.
// Correr una clase tras otra cuesta unos segundos más y elimina esa intermitencia.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
