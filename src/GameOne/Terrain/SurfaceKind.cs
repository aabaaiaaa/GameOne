namespace GameOne;

/// <summary>What the ground is made of at a point, which sets its colour and how it grips. Stored one byte a texel in the map.</summary>
internal enum SurfaceKind : byte { Scrub, Grass, Desert, Rock, Snow, Beach, Seabed, Site, PackIce, FloeField }

/// <summary>The climate of a named sea, which sets the colour and warmth of its water.</summary>
internal enum SeaKind : byte { Warm, Temperate, Icy }
