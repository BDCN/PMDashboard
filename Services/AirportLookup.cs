namespace PMDashboard.Services;

/// <summary>
/// Static IATA airport code lookup.
/// GeoRegion "East"  = Eastern + Central US time zones, South/Central America, Europe, Africa, Middle East.
/// GeoRegion "West"  = Mountain + Pacific + Alaska + Hawaii, Asia, Pacific Rim, Oceania.
/// Returns null for unrecognised codes — callers should prompt the user.
/// </summary>
public static class AirportLookup
{
    public sealed record AirportInfo(string Name, string StateOrCountry, string GeoRegion);

    public static AirportInfo? Lookup(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        return _airports.TryGetValue(code.Trim().ToUpper(), out var info) ? info : null;
    }

    // ── US Eastern time zone → East ────────────────────────────────────────────
    private static readonly Dictionary<string, AirportInfo> _airports = new(StringComparer.OrdinalIgnoreCase)
    {
        // Connecticut
        { "BDL", new("Bradley International",        "CT", "East") },
        { "HVN", new("Tweed New Haven",              "CT", "East") },
        // Delaware
        { "ILG", new("Wilmington",                   "DE", "East") },
        // Florida
        { "DAB", new("Daytona Beach",                "FL", "East") },
        { "ECP", new("Northwest Florida Beaches",    "FL", "East") },
        { "FLL", new("Fort Lauderdale",              "FL", "East") },
        { "GNV", new("Gainesville Regional",         "FL", "East") },
        { "JAX", new("Jacksonville",                 "FL", "East") },
        { "MCO", new("Orlando International",        "FL", "East") },
        { "MIA", new("Miami International",          "FL", "East") },
        { "PBI", new("Palm Beach International",     "FL", "East") },
        { "PNS", new("Pensacola",                    "FL", "East") },
        { "RSW", new("Southwest Florida",            "FL", "East") },
        { "SFB", new("Orlando Sanford",              "FL", "East") },
        { "SRQ", new("Sarasota Bradenton",           "FL", "East") },
        { "TLH", new("Tallahassee",                  "FL", "East") },
        { "TPA", new("Tampa International",          "FL", "East") },
        // Georgia
        { "AGS", new("Augusta Regional",             "GA", "East") },
        { "ATL", new("Hartsfield-Jackson Atlanta",   "GA", "East") },
        { "CSG", new("Columbus Metropolitan",        "GA", "East") },
        { "SAV", new("Savannah/Hilton Head",         "GA", "East") },
        // Indiana
        { "EVV", new("Evansville Regional",          "IN", "East") },
        { "FWA", new("Fort Wayne",                   "IN", "East") },
        { "IND", new("Indianapolis",                 "IN", "East") },
        { "SBN", new("South Bend",                   "IN", "East") },
        // Kentucky
        { "CVG", new("Cincinnati/Northern Kentucky", "KY", "East") },
        { "LEX", new("Blue Grass",                   "KY", "East") },
        { "SDF", new("Louisville Muhammad Ali",      "KY", "East") },
        // Maine
        { "BHB", new("Bar Harbor",                   "ME", "East") },
        { "BGR", new("Bangor",                        "ME", "East") },
        { "PWM", new("Portland",                     "ME", "East") },
        // Maryland
        { "BWI", new("Baltimore/Washington",         "MD", "East") },
        { "HGR", new("Hagerstown Regional",          "MD", "East") },
        // Massachusetts
        { "BOS", new("Logan International Boston",  "MA", "East") },
        { "HYA", new("Barnstable Municipal",         "MA", "East") },
        { "MVY", new("Martha's Vineyard",            "MA", "East") },
        { "ORH", new("Worcester Regional",           "MA", "East") },
        // Michigan
        { "AZO", new("Kalamazoo Battle Creek",       "MI", "East") },
        { "DTW", new("Detroit Metropolitan",         "MI", "East") },
        { "FNT", new("Bishop International Flint",   "MI", "East") },
        { "GRR", new("Gerald R Ford Grand Rapids",   "MI", "East") },
        { "LAN", new("Capital Region Lansing",       "MI", "East") },
        { "MBS", new("MBS International",            "MI", "East") },
        // New Hampshire
        { "MHT", new("Manchester-Boston Regional",  "NH", "East") },
        { "PSM", new("Portsmouth",                   "NH", "East") },
        // New Jersey
        { "ACY", new("Atlantic City",                "NJ", "East") },
        { "EWR", new("Newark Liberty",              "NJ", "East") },
        // New York
        { "ALB", new("Albany",                       "NY", "East") },
        { "BUF", new("Buffalo Niagara",              "NY", "East") },
        { "ELM", new("Elmira Corning Regional",      "NY", "East") },
        { "HPN", new("Westchester County",           "NY", "East") },
        { "ISP", new("Long Island MacArthur",        "NY", "East") },
        { "ITH", new("Ithaca Tompkins",              "NY", "East") },
        { "JFK", new("John F Kennedy International","NY", "East") },
        { "LGA", new("LaGuardia",                   "NY", "East") },
        { "ROC", new("Greater Rochester",            "NY", "East") },
        { "SWF", new("Stewart International",        "NY", "East") },
        { "SYR", new("Syracuse Hancock",             "NY", "East") },
        // North Carolina
        { "AVL", new("Asheville Regional",           "NC", "East") },
        { "CLT", new("Charlotte Douglas",            "NC", "East") },
        { "FAY", new("Fayetteville Regional",        "NC", "East") },
        { "GSO", new("Piedmont Triad Greensboro",    "NC", "East") },
        { "GSP", new("Greenville-Spartanburg",       "SC", "East") },
        { "ILM", new("Wilmington",                   "NC", "East") },
        { "OAJ", new("Albert J Ellis",              "NC", "East") },
        { "RDU", new("Raleigh-Durham",               "NC", "East") },
        // Ohio
        { "CAK", new("Akron-Canton",                "OH", "East") },
        { "CMH", new("Columbus John Glenn",          "OH", "East") },
        { "CLE", new("Cleveland Hopkins",            "OH", "East") },
        { "DAY", new("Dayton",                       "OH", "East") },
        { "LCK", new("Rickenbacker Columbus",        "OH", "East") },
        { "MFD", new("Mansfield Lahm",              "OH", "East") },
        { "OSU", new("Ohio State University",        "OH", "East") },
        { "TOL", new("Toledo Express",               "OH", "East") },
        { "YNG", new("Youngstown-Warren",            "OH", "East") },
        // Pennsylvania
        { "ABE", new("Lehigh Valley",               "PA", "East") },
        { "AGC", new("Allegheny County",             "PA", "East") },
        { "AVP", new("Wilkes-Barre Scranton",        "PA", "East") },
        { "ERI", new("Erie International",           "PA", "East") },
        { "HBG", new("Harrisburg",                   "PA", "East") },
        { "MDT", new("Harrisburg Int'l",            "PA", "East") },
        { "PHL", new("Philadelphia",                 "PA", "East") },
        { "PIT", new("Pittsburgh",                   "PA", "East") },
        // Rhode Island
        { "PVD", new("T F Green Providence",        "RI", "East") },
        // South Carolina
        { "CAE", new("Columbia Metropolitan",        "SC", "East") },
        { "CHS", new("Charleston",                   "SC", "East") },
        { "MYR", new("Myrtle Beach",                 "SC", "East") },
        // Tennessee
        { "BNA", new("Nashville",                    "TN", "East") },
        { "CHA", new("Chattanooga",                  "TN", "East") },
        { "MEM", new("Memphis",                      "TN", "East") },
        { "TRI", new("Tri Cities",                   "TN", "East") },
        { "TYS", new("McGhee Tyson Knoxville",       "TN", "East") },
        // Vermont
        { "BTV", new("Burlington",                   "VT", "East") },
        // Virginia
        { "CHO", new("Charlottesville-Albemarle",   "VA", "East") },
        { "DCA", new("Ronald Reagan Washington",    "VA", "East") },
        { "HEF", new("Manassas Regional",            "VA", "East") },
        { "IAD", new("Dulles International",         "VA", "East") },
        { "LYH", new("Lynchburg Regional",           "VA", "East") },
        { "ORF", new("Norfolk",                      "VA", "East") },
        { "PHF", new("Newport News/Williamsburg",    "VA", "East") },
        { "RIC", new("Richmond",                     "VA", "East") },
        { "ROA", new("Roanoke",                      "VA", "East") },
        // Washington DC
        { "DCA2",new("Washington National",          "DC", "East") },  // alias
        // West Virginia
        { "CRW", new("Yeager Charleston WV",         "WV", "East") },
        { "MGW", new("Morgantown",                   "WV", "East") },

        // ── US Central time zone → East ─────────────────────────────────────────
        // Alabama
        { "BHM", new("Birmingham-Shuttlesworth",    "AL", "East") },
        { "HSV", new("Huntsville",                   "AL", "East") },
        { "MOB", new("Mobile Regional",              "AL", "East") },
        { "MGM", new("Montgomery",                   "AL", "East") },
        // Arkansas
        { "BYH", new("Arkansas Intl",                "AR", "East") },
        { "FSM", new("Fort Smith",                   "AR", "East") },
        { "LIT", new("Bill & Hillary Clinton",       "AR", "East") },
        { "XNA", new("Northwest Arkansas",           "AR", "East") },
        // Illinois
        { "BMI", new("Central Illinois",             "IL", "East") },
        { "CMI", new("University of Illinois",       "IL", "East") },
        { "MDW", new("Midway Chicago",               "IL", "East") },
        { "ORD", new("O'Hare Chicago",              "IL", "East") },
        { "PIA", new("General Wayne Peoria",         "IL", "East") },
        { "SPI", new("Abraham Lincoln Springfield",  "IL", "East") },
        // Iowa
        { "CID", new("Eastern Iowa Cedar Rapids",    "IA", "East") },
        { "DSM", new("Des Moines",                   "IA", "East") },
        { "SUX", new("Sioux Gateway",                "IA", "East") },
        // Kansas
        { "ICT", new("Wichita Dwight Eisenhower",    "KS", "East") },
        { "MHK", new("Manhattan Regional",           "KS", "East") },
        { "TOP", new("Philip Billard Topeka",        "KS", "East") },
        // Louisiana
        { "AEX", new("Alexandria",                   "LA", "East") },
        { "BRO", new("Brownsville-South Padre",     "TX", "East") },
        { "BTR", new("Baton Rouge",                  "LA", "East") },
        { "LFT", new("Lafayette Regional",           "LA", "East") },
        { "MSY", new("Louis Armstrong New Orleans",  "LA", "East") },
        { "SHV", new("Shreveport",                   "LA", "East") },
        // Minnesota
        { "DLH", new("Duluth",                       "MN", "East") },
        { "FAR", new("Hector Fargo",                 "ND", "East") },
        { "MSP", new("Minneapolis-Saint Paul",       "MN", "East") },
        { "RST", new("Rochester",                    "MN", "East") },
        { "STC", new("St Cloud",                     "MN", "East") },
        // Mississippi
        { "GPT", new("Gulfport-Biloxi",             "MS", "East") },
        { "JAN", new("Jackson-Medgar Wiley Evers",  "MS", "East") },
        { "MEI", new("Key Field Meridian",           "MS", "East") },
        // Missouri
        { "COU", new("Columbia Regional",            "MO", "East") },
        { "MCI", new("Kansas City",                  "MO", "East") },
        { "SGF", new("Springfield-Branson",          "MO", "East") },
        { "STL", new("St Louis Lambert",             "MO", "East") },
        // Nebraska
        { "LNK", new("Lincoln",                      "NE", "East") },
        { "OMA", new("Omaha Eppley",                "NE", "East") },
        // North Dakota
        { "BIS", new("Bismarck",                     "ND", "East") },
        { "GFK", new("Grand Forks",                  "ND", "East") },
        // Oklahoma
        { "LAW", new("Lawton-Fort Sill",             "OK", "East") },
        { "OKC", new("Will Rogers Oklahoma City",    "OK", "East") },
        { "TUL", new("Tulsa",                        "OK", "East") },
        // South Dakota
        { "ABR", new("Aberdeen",                     "SD", "East") },
        { "FSD", new("Sioux Falls",                  "SD", "East") },
        { "RAP", new("Rapid City Regional",          "SD", "East") },
        // Texas (Central)
        { "ABI", new("Abilene Regional",             "TX", "East") },
        { "AMA", new("Rick Husband Amarillo",        "TX", "East") },
        { "AUS", new("Austin-Bergstrom",             "TX", "East") },
        { "BPT", new("Jack Brooks Beaumont",         "TX", "East") },
        { "CLL", new("Easterwood College Station",   "TX", "East") },
        { "CRP", new("Corpus Christi",               "TX", "East") },
        { "DAL", new("Dallas Love Field",            "TX", "East") },
        { "DFW", new("Dallas Fort Worth",            "TX", "East") },
        { "ELP", new("El Paso",                      "TX", "East") }, // Mountain but TX = East for business
        { "GGG", new("East Texas Regional",          "TX", "East") },
        { "GRK", new("Robert Gray Killeen",          "TX", "East") },
        { "HOU", new("Houston Hobby",                "TX", "East") },
        { "IAH", new("Houston George Bush",          "TX", "East") },
        { "LBB", new("Lubbock Preston Smith",        "TX", "East") },
        { "LRD", new("Laredo",                       "TX", "East") },
        { "MAF", new("Midland",                      "TX", "East") },
        { "MFE", new("McAllen Miller",               "TX", "East") },
        { "SAT", new("San Antonio",                  "TX", "East") },
        { "SJT", new("San Angelo",                   "TX", "East") },
        { "SPS", new("Wichita Falls",                "TX", "East") },
        { "TYR", new("Tyler Pounds Regional",        "TX", "East") },
        // Wisconsin
        { "ATW", new("Appleton",                     "WI", "East") },
        { "GRB", new("Green Bay Austin Straubel",    "WI", "East") },
        { "LSE", new("La Crosse",                    "WI", "East") },
        { "MKE", new("Milwaukee Mitchell",           "WI", "East") },
        { "MSN", new("Dane County Madison",          "WI", "East") },

        // ── US Mountain time zone → West ────────────────────────────────────────
        // Arizona
        { "FLG", new("Flagstaff Pulliam",            "AZ", "West") },
        { "PHX", new("Phoenix Sky Harbor",           "AZ", "West") },
        { "TUC", new("Tucson",                       "AZ", "West") },
        { "YUM", new("Yuma",                         "AZ", "West") },
        // Colorado
        { "APA", new("Centennial",                   "CO", "West") },
        { "ASE", new("Aspen Pitkin County",          "CO", "West") },
        { "COS", new("Colorado Springs",             "CO", "West") },
        { "DEN", new("Denver",                       "CO", "West") },
        { "DRO", new("Durango La Plata",             "CO", "West") },
        { "EGE", new("Eagle County",                 "CO", "West") },
        { "GJT", new("Grand Junction",               "CO", "West") },
        { "GUC", new("Gunnison",                     "CO", "West") },
        { "HDN", new("Yampa Valley",                 "CO", "West") },
        // Idaho
        { "BOI", new("Boise",                        "ID", "West") },
        { "IDA", new("Idaho Falls",                  "ID", "West") },
        { "LWS", new("Lewiston",                     "ID", "West") },
        { "PIH", new("Pocatello",                    "ID", "West") },
        { "SUN", new("Friedman Memorial",            "ID", "West") },
        // Montana
        { "BIL", new("Billings Logan",               "MT", "West") },
        { "BZN", new("Bozeman Yellowstone",          "MT", "West") },
        { "FCA", new("Glacier Park Kalispell",       "MT", "West") },
        { "GPI", new("Glacier Park Intl",            "MT", "West") },
        { "GTF", new("Great Falls",                  "MT", "West") },
        { "HLN", new("Helena",                       "MT", "West") },
        { "MSO", new("Missoula",                     "MT", "West") },
        // Nevada
        { "LAS", new("Harry Reid Las Vegas",         "NV", "West") },
        { "RNO", new("Reno-Tahoe",                   "NV", "West") },
        // New Mexico
        { "ABQ", new("Albuquerque Sunport",          "NM", "West") },
        { "ROW", new("Roswell",                      "NM", "West") },
        { "SAF", new("Santa Fe Regional",            "NM", "West") },
        // Utah
        { "CDC", new("Cedar City Regional",          "UT", "West") },
        { "OGD", new("Ogden",                        "UT", "West") },
        { "PVU", new("Provo",                        "UT", "West") },
        { "SGU", new("St George Regional",           "UT", "West") },
        { "SLC", new("Salt Lake City",               "UT", "West") },
        // Wyoming
        { "CPR", new("Natrona County Casper",        "WY", "West") },
        { "JAC", new("Jackson Hole",                 "WY", "West") },
        { "LAR", new("Laramie Regional",             "WY", "West") },
        { "RIW", new("Riverton Regional",            "WY", "West") },

        // ── US Pacific time zone → West ──────────────────────────────────────────
        // California
        { "ACV", new("Arcata Eureka",                "CA", "West") },
        { "BUR", new("Hollywood Burbank",            "CA", "West") },
        { "FAT", new("Fresno Yosemite",              "CA", "West") },
        { "LAX", new("Los Angeles",                  "CA", "West") },
        { "LGB", new("Long Beach",                   "CA", "West") },
        { "MRY", new("Monterey Regional",            "CA", "West") },
        { "OAK", new("Oakland",                      "CA", "West") },
        { "ONT", new("Ontario",                      "CA", "West") },
        { "PSP", new("Palm Springs",                 "CA", "West") },
        { "RDD", new("Redding Municipal",            "CA", "West") },
        { "SAN", new("San Diego",                    "CA", "West") },
        { "SBA", new("Santa Barbara",               "CA", "West") },
        { "SFO", new("San Francisco",               "CA", "West") },
        { "SJC", new("San Jose",                     "CA", "West") },
        { "SMF", new("Sacramento",                   "CA", "West") },
        { "SNA", new("John Wayne Orange County",     "CA", "West") },
        { "STS", new("Charles M Schulz Sonoma",     "CA", "West") },
        // Oregon
        { "EUG", new("Eugene",                       "OR", "West") },
        { "MFR", new("Rogue Valley Medford",         "OR", "West") },
        { "PDT", new("Eastern Oregon Regional",      "OR", "West") },
        { "PDX", new("Portland",                     "OR", "West") },
        { "RDM", new("Roberts Field Redmond",        "OR", "West") },
        // Washington
        { "BFI", new("Boeing Field King County",     "WA", "West") },
        { "GEG", new("Spokane",                      "WA", "West") },
        { "PAE", new("Paine Field Everett",          "WA", "West") },
        { "SEA", new("Seattle-Tacoma",               "WA", "West") },
        { "YKM", new("Yakima Air Terminal",          "WA", "West") },

        // ── Alaska & Hawaii → West ────────────────────────────────────────────────
        { "ANC", new("Ted Stevens Anchorage",        "AK", "West") },
        { "FAI", new("Fairbanks",                    "AK", "West") },
        { "JNU", new("Juneau",                       "AK", "West") },
        { "KTN", new("Ketchikan",                    "AK", "West") },
        { "SIT", new("Sitka Rocky Gutierrez",        "AK", "West") },
        { "HNL", new("Daniel K Inouye Honolulu",     "HI", "West") },
        { "ITO", new("Hilo",                         "HI", "West") },
        { "KOA", new("Ellison Onizuka Kailua-Kona",  "HI", "West") },
        { "LIH", new("Lihue",                        "HI", "West") },
        { "OGG", new("Kahului Maui",                 "HI", "West") },

        // ── Canada → East (east of Rockies) or West (west of Rockies) ────────────
        { "YUL", new("Montreal Trudeau",            "QC", "East") },
        { "YYZ", new("Toronto Pearson",              "ON", "East") },
        { "YOW", new("Ottawa",                       "ON", "East") },
        { "YHZ", new("Halifax",                      "NS", "East") },
        { "YYC", new("Calgary",                      "AB", "West") },
        { "YEG", new("Edmonton",                     "AB", "West") },
        { "YVR", new("Vancouver",                    "BC", "West") },
        { "YWG", new("Winnipeg",                     "MB", "East") },

        // ── Mexico / Central America / Caribbean → East ───────────────────────────
        { "MEX", new("Mexico City",                  "Mexico",   "East") },
        { "CUN", new("Cancun",                       "Mexico",   "East") },
        { "GDL", new("Guadalajara",                  "Mexico",   "East") },
        { "MTY", new("Monterrey",                    "Mexico",   "East") },
        { "TIJ", new("Tijuana",                      "Mexico",   "East") },
        { "MGA", new("Managua",                      "Nicaragua","East") },
        { "SJO", new("Juan Santamaria Costa Rica",   "CR",       "East") },
        { "GUA", new("La Aurora Guatemala City",     "Guatemala","East") },
        { "SAP", new("Ramon Villeda San Pedro Sula", "Honduras", "East") },
        { "MBJ", new("Sangster Montego Bay",         "Jamaica",  "East") },
        { "NAS", new("Nassau",                       "Bahamas",  "East") },
        { "BGI", new("Grantley Adams Barbados",      "Barbados", "East") },
        { "SDQ", new("Las Americas Santo Domingo",   "DR",       "East") },
        { "SJU", new("Luis Munoz Marin San Juan",    "PR",       "East") },
        { "POS", new("Piarco Trinidad",              "Trinidad", "East") },

        // ── South America → East (per user: Brazil = East) ───────────────────────
        { "GRU", new("Guarulhos São Paulo",          "Brazil",    "East") },
        { "GIG", new("Galeão Rio de Janeiro",        "Brazil",    "East") },
        { "BSB", new("Brasilia",                     "Brazil",    "East") },
        { "CGH", new("Congonhas São Paulo",          "Brazil",    "East") },
        { "FOR", new("Pinto Martins Fortaleza",      "Brazil",    "East") },
        { "REC", new("Guararapes Recife",            "Brazil",    "East") },
        { "POA", new("Salgado Filho Porto Alegre",   "Brazil",    "East") },
        { "EZE", new("Ezeiza Buenos Aires",          "Argentina", "East") },
        { "AEP", new("Aeroparque Buenos Aires",      "Argentina", "East") },
        { "SCL", new("Comodoro Arturo Pudahuel",     "Chile",     "East") },
        { "BOG", new("El Dorado Bogota",             "Colombia",  "East") },
        { "MDE", new("José María Córdova Medellin",  "Colombia",  "East") },
        { "LIM", new("Jorge Chavez Lima",            "Peru",      "East") },
        { "UIO", new("Mariscal Quito",               "Ecuador",   "East") },
        { "GYE", new("Simon Bolivar Guayaquil",      "Ecuador",   "East") },
        { "MVD", new("Carrasco Montevideo",           "Uruguay",   "East") },
        { "ASU", new("Silvio Pettirossi Asuncion",   "Paraguay",  "East") },
        { "CCS", new("Simón Bolívar Caracas",        "Venezuela", "East") },

        // ── Europe → East ─────────────────────────────────────────────────────────
        { "LHR", new("Heathrow London",              "UK",      "East") },
        { "LGW", new("Gatwick London",               "UK",      "East") },
        { "MAN", new("Manchester",                   "UK",      "East") },
        { "EDI", new("Edinburgh",                    "UK",      "East") },
        { "CDG", new("Charles de Gaulle Paris",      "France",  "East") },
        { "ORY", new("Orly Paris",                   "France",  "East") },
        { "FRA", new("Frankfurt",                    "Germany", "East") },
        { "MUC", new("Munich",                       "Germany", "East") },
        { "TXL", new("Tegel Berlin",                 "Germany", "East") },
        { "BER", new("Berlin Brandenburg",           "Germany", "East") },
        { "AMS", new("Schiphol Amsterdam",           "Netherlands","East") },
        { "MAD", new("Barajas Madrid",              "Spain",   "East") },
        { "BCN", new("El Prat Barcelona",            "Spain",   "East") },
        { "FCO", new("Fiumicino Rome",               "Italy",   "East") },
        { "MXP", new("Malpensa Milan",               "Italy",   "East") },
        { "ATH", new("Eleftherios Venizelos Athens", "Greece",  "East") },
        { "IST", new("Istanbul",                     "Turkey",  "East") },
        { "ZRH", new("Zurich",                       "Switzerland","East") },
        { "VIE", new("Vienna",                       "Austria", "East") },
        { "BRU", new("Brussels",                     "Belgium", "East") },
        { "ARN", new("Arlanda Stockholm",            "Sweden",  "East") },
        { "OSL", new("Gardermoen Oslo",              "Norway",  "East") },
        { "CPH", new("Kastrup Copenhagen",           "Denmark", "East") },
        { "HEL", new("Helsinki Vantaa",              "Finland", "East") },
        { "WAW", new("Chopin Warsaw",                "Poland",  "East") },
        { "PRG", new("Vaclav Havel Prague",          "CZ",      "East") },
        { "BUD", new("Liszt Budapest",               "Hungary", "East") },
        { "LIS", new("Humberto Delgado Lisbon",      "Portugal","East") },
        { "OPO", new("Francisco Sa Carneiro Porto",  "Portugal","East") },
        { "DUB", new("Dublin",                       "Ireland", "East") },

        // ── Middle East / Africa → East ───────────────────────────────────────────
        { "DXB", new("Dubai",                        "UAE",          "East") },
        { "AUH", new("Abu Dhabi",                    "UAE",          "East") },
        { "DOH", new("Hamad Doha",                   "Qatar",        "East") },
        { "RUH", new("King Khalid Riyadh",           "Saudi Arabia", "East") },
        { "JED", new("King Abdulaziz Jeddah",        "Saudi Arabia", "East") },
        { "TLV", new("Ben Gurion Tel Aviv",           "Israel",       "East") },
        { "CAI", new("Cairo",                        "Egypt",        "East") },
        { "JNB", new("OR Tambo Johannesburg",        "S Africa",     "East") },
        { "CPT", new("Cape Town",                    "S Africa",     "East") },
        { "NBO", new("Jomo Kenyatta Nairobi",        "Kenya",        "East") },
        { "LOS", new("Murtala Muhammed Lagos",       "Nigeria",      "East") },
        { "ADD", new("Bole Addis Ababa",             "Ethiopia",     "East") },
        { "ACC", new("Kotoka Accra",                 "Ghana",        "East") },
        { "CMN", new("Mohammed V Casablanca",        "Morocco",      "East") },
        { "TUN", new("Carthage Tunis",               "Tunisia",      "East") },

        // ── Asia → West ──────────────────────────────────────────────────────────
        { "NRT", new("Narita Tokyo",                 "Japan",     "West") },
        { "HND", new("Haneda Tokyo",                 "Japan",     "West") },
        { "KIX", new("Kansai Osaka",                 "Japan",     "West") },
        { "ICN", new("Incheon Seoul",                "S Korea",   "West") },
        { "GMP", new("Gimpo Seoul",                  "S Korea",   "West") },
        { "PEK", new("Capital Beijing",              "China",     "West") },
        { "PKX", new("Daxing Beijing",               "China",     "West") },
        { "PVG", new("Pudong Shanghai",              "China",     "West") },
        { "SHA", new("Hongqiao Shanghai",            "China",     "West") },
        { "CAN", new("Baiyun Guangzhou",             "China",     "West") },
        { "HKG", new("Hong Kong",                   "HK",        "West") },
        { "TPE", new("Taoyuan Taipei",               "Taiwan",    "West") },
        { "MNL", new("Ninoy Aquino Manila",          "Philippines","West") },
        { "BKK", new("Suvarnabhumi Bangkok",         "Thailand",  "West") },
        { "SGN", new("Tan Son Nhat Ho Chi Minh",     "Vietnam",   "West") },
        { "HAN", new("Noi Bai Hanoi",                "Vietnam",   "West") },
        { "SIN", new("Changi Singapore",             "Singapore", "West") },
        { "KUL", new("Kuala Lumpur",                 "Malaysia",  "West") },
        { "CGK", new("Soekarno-Hatta Jakarta",       "Indonesia", "West") },
        { "DEL", new("Indira Gandhi New Delhi",      "India",     "West") },
        { "BOM", new("Chhatrapati Shivaji Mumbai",   "India",     "West") },
        { "MAA", new("Chennai",                      "India",     "West") },
        { "BLR", new("Kempegowda Bangalore",         "India",     "West") },

        // ── Oceania / Pacific → West ─────────────────────────────────────────────
        { "SYD", new("Kingsford Smith Sydney",       "Australia", "West") },
        { "MEL", new("Melbourne",                    "Australia", "West") },
        { "BNE", new("Brisbane",                     "Australia", "West") },
        { "PER", new("Perth",                        "Australia", "West") },
        { "ADL", new("Adelaide",                     "Australia", "West") },
        { "AKL", new("Auckland",                     "NZ",        "West") },
        { "CHC", new("Christchurch",                 "NZ",        "West") },
        { "PPT", new("Faa'a Papeete Tahiti",        "Fr Poly",   "West") },
        { "NAN", new("Nadi Fiji",                    "Fiji",      "West") },
        { "GUM", new("Antonio B Won Pat Guam",       "Guam",      "West") },
    };
}
