namespace Fonoteca.Domain.Catalogue;

/// <summary>
/// Fonoteca's own genres: a short tree of two levels, and every MusicBrainz
/// genre folded into one place in it.
/// </summary>
/// <remarks>
/// <b>Written by hand, the owner's choice.</b> MusicBrainz names 2,216 genres
/// and states which is a subgenre of which only on its web pages, never over
/// WS/2; Wikidata's tree is general-purpose and puts a third of this library's
/// genres under two parents. So the tree is this file: Roon's top level (after
/// AllMusic) with Rock, Pop and Metal apart, about a hundred names in all, and
/// each MusicBrainz genre listed under the one it folds into — drafted from
/// MusicBrainz's own "subgenre of" and "fusion of", and settled by hand where
/// those said nothing.
///
/// <b>A fusion sits under both its tops</b> (Blues Rock under Blues and Rock),
/// and a genre carried is carried with its tops, so a player that lists genres
/// flat still finds every blues album under Blues. A name this file does not
/// know is <see cref="Other"/> until it is placed here.
///
/// <b>The catalogue keeps MusicBrainz's genres; this is read over them</b>, so
/// moving a genre here re-files every album the next time it is read, with no
/// lookup. What is cached is answers, never rankings.
/// </remarks>
public static class Genres
{
    public const string Other = "Other";

    /// <param name="Tops">The tops it sits under: none for a top, two for a fusion.</param>
    /// <param name="MusicBrainz">The MusicBrainz genres that fold into it, as MusicBrainz spells them.</param>
    private sealed record Genre(string Name, string[] Tops, string[] MusicBrainz);

    private static Genre Top(string name, params string[] musicBrainz) => new(name, [], musicBrainz);

    private static Genre Sub(string name, string[] tops, params string[] musicBrainz) => new(name, tops, musicBrainz);

    private static readonly Genre[] Tree =
    [
        Top(
            "Rock",
            "acoustic rock", "afro rock", "alpenrock", "anatolian rock", "bard rock", "beat rock", "comedy rock",
            "dance-rock", "eleki", "experimental rock", "frat rock", "freakbeat", "garage psych", "garage rock",
            "garage rock revival", "glam", "hot rod music", "instrumental rock", "jam band", "krautrock", "latin rock",
            "livetronica", "loud kei", "mainstream rock", "mangue beat", "math pop", "math rock", "miejski folk", "mod",
            "mod revival", "neo-rockabilly", "no wave", "noise rock", "occult rock", "piano rock", "pigfuck",
            "pop yeh-yeh", "post-rock", "proto-punk", "rautalanka", "reggae rock", "rock", "rock andaluz",
            "rock andino", "rock opera", "rock urbano", "rock urbano mexicano", "soft visual", "sufi rock", "surf",
            "surf rock", "visual kei", "vocal surf", "zolo"),
        Sub("Classic Rock", ["Rock"], "aor", "arena rock", "classic rock", "glam punk", "glam rock"),
        Sub("Hard Rock", ["Rock"], "desert rock", "hard rock", "rock jiwang", "rock kapak", "stoner rock"),
        Sub("Soft Rock", ["Rock"], "soft rock", "yacht rock"),
        Sub(
            "Alternative Rock", ["Rock"],
            "alternative dance", "alternative rock", "britpop", "coldwave", "dance-punk", "dance-punk revival",
            "dark wave", "dream pop", "emo", "emo pop", "emocore", "ethereal wave", "geek rock", "gothic",
            "gothic rock", "grebo", "grunge", "j-rock", "madchester", "midwest emo", "neoclassical dark wave",
            "neue deutsche todeskunst", "neue deutsche welle", "new rave", "new romantic", "new wave", "post-britpop",
            "post-grunge", "post-punk", "post-punk revival", "shoegaze", "yakousei"),
        Sub(
            "Indie Rock", ["Rock"],
            "dunedin sound", "hamburger schule", "indie rock", "indie surf", "lo-fi", "noise pop", "shitgaze",
            "slacker rock", "slowcore"),
        Sub(
            "Progressive Rock", ["Rock"],
            "art rock", "avant-prog", "brutal prog", "crossover prog", "neo-progressive rock", "progressive",
            "progressive rock", "symphonic prog", "symphonic rock", "zeuhl"),
        Sub(
            "Psychedelic Rock", ["Rock"],
            "acid rock", "canterbury scene", "heavy psych", "neo-psychedelia", "paisley underground",
            "psichedelia occulta italiana", "psychedelic", "psychedelic rock", "psychploitation", "raga rock",
            "space rock", "space rock revival", "zamrock"),
        Sub("Southern Rock", ["Rock"], "southern rock"),
        Sub("Roots Rock", ["Rock"], "heartland rock", "pub rock", "roots rock", "swamp rock", "tex-mex"),
        Sub("Rock & Roll", ["Rock"], "indorock", "rock and roll", "rockaballad", "rockabilly"),
        Sub(
            "Punk", ["Rock"],
            "alternative punk", "anarcho-punk", "art punk", "beatdown hardcore", "blackened crust", "budget rock",
            "burning spirits", "celtic punk", "christian hardcore", "cowpunk", "crack rock steady", "crust punk",
            "d-beat", "deathrock", "digital hardcore", "easycore", "electropunk", "emoviolence", "folk punk",
            "garage punk", "gypsy punk", "hardcore punk", "hopepunk", "horror punk", "könsrock", "melodic hardcore",
            "neocrust", "neon pop punk", "nintendocore", "noisecore", "oi", "pop punk", "pop screamo", "post-hardcore",
            "powerviolence", "progressive post-hardcore", "psychobilly", "punk", "punk rock", "queercore", "raw punk",
            "riot grrrl", "rustic stomp", "sasscore", "screamo", "seishun punk", "ska punk", "skacore", "skate punk",
            "stenchcore", "street punk", "surf punk", "swancore", "thrashcore", "tough guy hardcore", "uk82",
            "viking rock"),
        Top(
            "Pop",
            "akishibu-kei", "ambient pop", "art pop", "avant-garde pop", "bardcore", "baroque pop", "bhojpuri pop",
            "bitpop", "bolero-beat", "brill building", "bubblegum pop", "c-pop", "canción melódica", "cantopop",
            "city pop", "dansband", "dansktop", "denpa", "electro hop", "europop", "flamenco pop", "gufeng", "hyperpop",
            "hypnagogic pop", "idol kayō", "indian pop", "j-pop", "jesus music", "k-pop", "kayōkyoku", "korean ballad",
            "levenslied", "mandopop", "moe song", "mood kayō", "mulatós", "música cebolla", "nederpop", "neo-acoustic",
            "neo-city pop", "nyū myūjikku", "opm", "oriental ballad", "orthodox pop", "palingsound", "partyschlager",
            "persian pop", "picopop", "pop", "pop ghazal", "pop kreatif", "pop minang", "progressive pop",
            "psychedelic pop", "q-pop", "rom kbach", "russian chanson", "schlager", "shibuya-kei", "sophisti-pop",
            "sundanese pop", "sunshine pop", "t-pop", "talempong goyang", "techno kayō", "toytown pop", "turkish pop",
            "v-pop", "volkstümliche musik", "wa euro", "wong shadow", "yé-yé", "zhongguo feng"),
        Sub(
            "Pop Rock", ["Pop", "Rock"],
            "beat music", "burmese stereo", "group sounds", "jangle pop", "jovem guarda", "manila sound", "merseybeat",
            "nederbeat", "pop rock", "power pop", "tropical rock"),
        Sub("Dance Pop", ["Pop"], "dance-pop", "romanian popcorn", "tecnorumba", "teen pop", "township bubblegum"),
        Sub(
            "Indie Pop", ["Pop"],
            "alternative pop", "bedroom pop", "c86", "chamber pop", "cuddlecore", "donosti sound", "indie pop",
            "tontipop", "toypop", "twee pop"),
        Sub("Adult Contemporary", ["Pop"], "ballad", "latin ballad"),
        Sub(
            "Singer-Songwriter", ["Pop"],
            "avtorskaya pesnya", "euskal kantagintza berria", "kleinkunst", "liedermacher", "música de intervenção",
            "nova cançó", "nueva canción", "nueva canción chilena", "nueva canción española", "nuevo cancionero",
            "singer-songwriter"),
        Sub("Synth-Pop", ["Pop", "Electronic"], "electropop", "synth-pop"),
        Top(
            "Metal",
            "atmospheric sludge metal", "avant-garde metal", "celtic metal", "djent", "doomgaze", "downtempo deathcore",
            "drone metal", "folk metal", "funk metal", "groove metal", "kawaii metal", "mathcore", "medieval metal",
            "melodic metalcore", "metal", "metalcore", "neoclassical metal", "post-metal", "power metal",
            "progressive metal", "progressive metalcore", "sludge metal", "southern metal", "stoner metal", "thall",
            "trance metal", "viking metal"),
        Sub("Heavy Metal", ["Metal"], "heavy metal", "nwobhm", "speed metal", "us power metal"),
        Sub("Alternative Metal", ["Metal"], "alternative metal", "neue deutsche härte", "nu metal"),
        Sub("Symphonic Metal", ["Metal"], "gothic metal", "symphonic metal"),
        Sub("Glam Metal", ["Metal"], "glam metal", "pop metal", "sleaze rock"),
        Sub(
            "Extreme Metal", ["Metal"],
            "atmospheric black metal", "black 'n' roll", "black metal", "blackened death metal", "blackgaze",
            "brutal death metal", "crossover thrash", "cybergrind", "death 'n' roll", "death metal", "death-doom metal",
            "deathcore", "deathgrind", "depressive black metal", "dissonant black metal", "dissonant death metal",
            "doom metal", "epic doom metal", "funeral doom metal", "goregrind", "gorenoise", "grindcore",
            "melodic black metal", "melodic death metal", "mincecore", "noisegrind", "old school death metal",
            "pagan black metal", "pornogrind", "slam death metal", "symphonic black metal", "technical death metal",
            "technical thrash metal", "thrash metal", "traditional doom metal", "war metal"),
        Top("Blues", "african blues", "blues", "classic blues", "hill country blues", "jazz blues", "vaudeville blues"),
        Sub("Blues Rock", ["Blues", "Rock"], "blues rock", "boogie rock", "british blues", "punk blues"),
        Sub(
            "Electric Blues", ["Blues"],
            "electric blues", "electric texas blues", "louisiana blues", "modern blues", "swamp blues", "texas blues"),
        Sub("Chicago Blues", ["Blues"], "acoustic chicago blues", "chicago blues"),
        Sub("Delta Blues", ["Blues"], "delta blues"),
        Sub(
            "Acoustic Blues", ["Blues"],
            "acoustic blues", "acoustic texas blues", "country blues", "jug band", "piedmont blues"),
        Sub("Piano Blues", ["Blues"], "boogie-woogie", "new orleans blues", "piano blues"),
        Sub("Soul Blues", ["Blues", "R&B"], "soul blues"),
        Top(
            "Jazz",
            "afro-jazz", "cape jazz", "classic jazz", "contemporary jazz", "crime jazz", "dark jazz", "ethio-jazz",
            "flamenco jazz", "gypsy jazz", "indo jazz", "instrumental jazz", "jazz", "kréyol djaz", "marabi",
            "mbaqanga", "modern creative", "neo-bop", "orchestral jazz", "paramaribop", "samba-jazz", "spiritual jazz",
            "stride", "sweet jazz", "third stream"),
        Sub("Jazz Fusion", ["Jazz", "Rock"], "acid jazz", "jazz fusion", "jazz rock", "jazz-funk"),
        Sub(
            "Early Jazz", ["Jazz"],
            "cakewalk", "classic ragtime", "coon song", "dixieland", "novelty piano", "ragtime", "ragtime song"),
        Sub("Swing", ["Jazz"], "swing", "swing revival", "west coast swing"),
        Sub("Big Band", ["Jazz"], "big band", "experimental big band"),
        Sub("Bebop", ["Jazz"], "bebop"),
        Sub("Hard Bop", ["Jazz"], "hard bop"),
        Sub("Cool Jazz", ["Jazz"], "cool jazz"),
        Sub("Post-Bop", ["Jazz"], "jazz mugham", "modal jazz", "post-bop"),
        Sub("Soul Jazz", ["Jazz"], "soul jazz"),
        Sub("Vocal Jazz", ["Jazz"], "jazz pop", "vocal jazz", "vocalese"),
        Sub("Smooth Jazz", ["Jazz"], "crossover jazz", "smooth jazz"),
        Sub("Free Jazz", ["Jazz", "Avant-Garde"], "avant-garde jazz", "free funk", "free jazz"),
        Sub("Latin Jazz", ["Jazz", "Latin"], "afro-cuban jazz", "latin jazz"),
        Top(
            "Classical",
            "art song", "bagatelle", "canzona", "capriccio", "character piece", "classical", "concertina band",
            "divertissement", "english pastoral school", "fantasia", "fugue", "impromptu", "lied", "lute song",
            "mélodie", "nocturne", "orchestral song", "polonaise", "prelude", "ricercar", "russian romance",
            "symphonic mugham", "theme and variations", "toccata", "waltz", "western classical", "étude"),
        Sub("Classical Crossover", ["Classical", "Pop"], "classical crossover", "operatic pop"),
        Sub(
            "Early Music", ["Classical"],
            "ambrosian chant", "anglican chant", "ars antiqua", "ars nova", "ars subtilior", "beneventan chant",
            "byzantine chant", "celtic chant", "church music", "contenance angloise", "gallican chant",
            "gregorian chant", "kontakion", "kyivan chant", "lauda", "madrigal", "medieval", "medieval lyric poetry",
            "minnesang", "mozarabic chant", "old roman chant", "plainchant", "renaissance",
            "russian orthodox liturgical music", "sarum chant", "sticheron", "taizé chant", "tiento", "troparion",
            "troubadour song", "valaam chant", "villancico", "zema", "znamenny chant"),
        Sub("Baroque", ["Classical"], "baroque", "baroque suite"),
        Sub("Classical Period", ["Classical"], "classical period"),
        Sub("Romantic", ["Classical"], "romantic classical"),
        Sub(
            "Modern Classical", ["Classical"],
            "american gamelan", "contemporary classical", "expressionism", "futurism", "holy minimalism",
            "impressionism", "indeterminacy", "integral serialism", "microtonal classical", "minimalism",
            "modern classical", "musique concrète instrumentale", "neoclassicism", "neoromanticism", "new complexity",
            "post-classical", "post-minimalism", "process music", "serialism", "sonorism", "spectralism",
            "stochastic music", "totalism"),
        Sub(
            "Opera", ["Classical"],
            "ballad opera", "grand opera", "género chico", "género grande", "monodrama", "opera", "opera buffa",
            "opera semiseria", "opera seria", "operetta", "opéra comique", "romantische oper", "singspiel",
            "tragédie en musique", "verismo", "zarzuela", "zarzuela barroca", "zeitoper"),
        Sub(
            "Orchestral", ["Classical"],
            "brass band", "british brass band", "circus march", "concert band", "concerto", "concerto for orchestra",
            "concerto grosso", "dechovka", "drum and bugle corps", "drumline", "funeral march", "march",
            "marching band", "música festera", "orchestral", "overture", "pep band", "serenade", "sinfonia concertante",
            "symphonic poem", "symphony"),
        Sub("Chamber Music", ["Classical"], "sonata", "string quartet"),
        Sub(
            "Choral", ["Classical"],
            "cantata", "choral symphony", "mass", "motet", "oratorio", "passion setting", "requiem"),
        Sub("Ballet", ["Classical"], "ballet", "ballet de cour", "comédie-ballet", "opera-ballet"),
        Top(
            "Country",
            "alternative country", "bakersfield sound", "classic country", "country", "country and irish",
            "country rap", "country soul", "country yodeling", "gothic country", "nashville sound", "texas country",
            "truck driving country", "urban cowboy", "western"),
        Sub("Country Rock", ["Country", "Rock"], "cosmic country", "country rock"),
        Sub("Country Pop", ["Country", "Pop"], "boyfriend country", "country pop", "countrypolitan"),
        Sub(
            "Traditional Country", ["Country"],
            "close harmony", "country boogie", "honky tonk", "neo-traditional country", "traditional country",
            "western swing"),
        Sub("Contemporary Country", ["Country"], "bro-country", "contemporary country"),
        Sub("Outlaw Country", ["Country"], "outlaw country", "progressive country", "red dirt"),
        Sub(
            "Bluegrass", ["Country"],
            "bluegrass", "bluegrass gospel", "jamgrass", "progressive bluegrass", "traditional bluegrass"),
        Sub("Americana", ["Country"], "americana"),
        Top("R&B", "beach music", "r&b", "swamp pop"),
        Sub(
            "Soul", ["R&B"],
            "blue-eyed soul", "chicago soul", "deep soul", "latin soul", "motown", "neo soul", "northern soul",
            "philly soul", "pop soul", "progressive soul", "psychedelic soul", "smooth soul", "soul", "southern soul"),
        Sub(
            "Funk", ["R&B"],
            "afro-funk", "boogie", "bounce beat", "brit funk", "deep funk", "electro-funk", "funk", "funk rock",
            "go-go", "latin funk", "minneapolis sound", "p-funk", "porn groove", "synth funk"),
        Sub(
            "Disco", ["R&B"],
            "disco", "electro-disco", "euro-disco", "hi-nrg", "italo-disco", "latin disco", "red disco", "space disco"),
        Sub(
            "Contemporary R&B", ["R&B"],
            "alternative r&b", "contemporary r&b", "hip hop soul", "new jack swing", "quiet storm", "trap soul",
            "uk street soul"),
        Sub("Early R&B", ["R&B"], "british rhythm & blues", "doo-wop", "jump blues", "new orleans r&b"),
        Top(
            "Hip Hop",
            "abstract hip hop", "afro trap", "afroswing", "ambient plugg", "arabesk rap", "asian rock", "battle rap",
            "battle record", "beatboxing", "bongo flava", "boom bap", "bounce", "chap hop", "chicago drill",
            "chicano rap", "chipmunk soul", "chopped and screwed", "christian hip hop", "cloud rap", "comedy hip hop",
            "conscious hip hop", "crunk", "crunkcore", "dark plugg", "detroit trap", "digicore", "dirty south", "drill",
            "drumless hip hop", "east coast hip hop", "emo rap", "experimental hip hop", "folkhop", "free car music",
            "g-funk", "genge", "gengetone", "glitch hop", "hip hop", "hipco", "hiplife", "horrorcore", "houston sound",
            "hyphy", "instrumental hip hop", "jazz rap", "jerk", "jerk rap", "jersey club rap", "jersey drill",
            "lo-fi hip hop", "lowend", "miami bass", "mobb music", "nerdcore", "new jazz", "new york drill",
            "no melody trap", "old school hip hop", "philly club rap", "philly drill", "phonk", "plugg", "pluggnb",
            "political hip hop", "punk rap", "rage", "ragga hip-hop", "rapcore", "rapso", "ratchet music", "regalia",
            "samba rap", "sample drill", "sexy drill", "sigilkore", "sissy bounce", "snap", "southern hip hop",
            "stoner rap", "techno bass", "terror plugg", "trap", "trap latino", "trap metal", "trap shaabi", "tread",
            "turntablism", "uk drill", "underground hip hop", "west coast hip hop"),
        Sub("Rap Rock", ["Hip Hop", "Rock"], "rap metal", "rap rock"),
        Sub("Pop Rap", ["Hip Hop"], "chicago bop", "frat rap", "pop rap"),
        Sub(
            "Hardcore Hip Hop", ["Hip Hop"],
            "britcore", "chopper", "coke rap", "dungeon rap", "gangsta rap", "hardcore hip hop", "mafioso rap",
            "memphis rap", "scam rap"),
        Sub("Alternative Hip Hop", ["Hip Hop"], "alternative hip hop"),
        Top(
            "Electronic",
            "acidcore", "acousmatic", "algorave", "amigacore", "aquacrunk", "artcore", "balani show", "baltimore club",
            "barber beats", "batida", "belgian techno", "berlin school", "bit music", "black midi", "bouncy techno",
            "breakcore", "broken beat", "broken transmission", "bubblegum bass", "bubblegum dance", "bubbling",
            "buchiage trance", "budots", "bytebeat", "bérite club", "celtic electronica", "chillsynth", "chillwave",
            "chiptune", "club", "comfy synth", "coupé-décalé", "cruise", "dance", "dariacore", "dark disco",
            "darkcore edm", "darksynth", "deathchant hardcore", "deconstructed club", "digital cumbia",
            "digital fusion", "doomcore", "doskpop", "dreampunk", "dreamwave", "drift phonk", "dubstyle",
            "dungeon chip", "dungeon synth", "dutch trancecore", "early hardstyle", "eccojams", "edm", "electro",
            "electro latino", "electro swing", "electroacoustic", "electroclash", "electronic", "electronic rock",
            "electronica", "electronicore", "electrotango", "euphoric hardstyle", "eurobeat", "eurodance",
            "experimental electronic", "extratone", "festival trap", "flashcore", "fm synthesis", "folktronica",
            "footwork", "frapcore", "freeform hardcore", "freestyle", "frenchcore", "frenchtek", "funkot",
            "funktronica", "future bass", "future core", "future rave", "futurepop", "gabber", "ghetto funk",
            "ghettotech", "glitch", "glitch hop edm", "glitch pop", "graphical sound", "grime", "hands up",
            "happy hardcore", "hard drum", "hard nrg", "hard renaissance", "hard trap", "hardcore techno", "hardstyle",
            "hardtekk", "hardvapour", "hardwave", "heaven trap", "hexd", "horror synth", "hybrid trap", "hyper techno",
            "hypertechno", "idm", "illbient", "indietronica", "industrial hardcore", "italo dance", "j-core", "j-euro",
            "jersey club", "jumpstyle", "jungle terror", "kawaii future bass", "keller synth", "krushclub", "kuduro",
            "leftfield", "lento violento", "lolicore", "mallsoft", "manyao", "mashcore", "melodic bass", "microsound",
            "midtempo bass", "minatory", "minimal synth", "minimal wave", "modern hardtek", "moogsploitation",
            "moombahcore", "moombahton", "musique concrète", "neo rave", "neo-grime", "nerdcore techno", "neurohop",
            "nightcore", "nortec", "nu disco", "nu jazz", "nu style gabber", "nustyle", "ori deck", "outrun",
            "philly club", "post-dubstep", "powerstomp", "progressive electronic", "psybient", "psystyle", "pumpcore",
            "raggacore", "raggatek", "rave", "rawphoric", "rawstyle", "sampledelia", "seapunk", "shangaan electro",
            "singeli", "skweee", "slimepunk", "slushwave", "sovietwave", "spacesynth", "speedcore", "splittercore",
            "synthwave", "terrorcore", "toytown techno", "trap edm", "tribal guarachero", "twerk", "uk funky",
            "uk hardcore", "uptempo hardcore", "utopian virtual", "vapornoise", "vaportrap", "vaporwave", "wave",
            "weightless", "winter synth", "witch house", "xtra raw"),
        Sub(
            "House", ["Electronic"],
            "3-step", "acid house", "afro house", "amapiano", "ambient house", "bacardi", "balearic beat",
            "ballroom house", "bass house", "big room house", "brazilian bass", "bubbling house", "changa tuki",
            "chicago house", "complextro", "deep house", "deep tech", "diva house", "donk", "dutch house",
            "electro house", "epic house", "euro house", "festival progressive house", "fidget house", "french electro",
            "french house", "funky house", "future bounce", "future funk", "future house", "g-house", "garage house",
            "ghetto house", "gospel house", "gqom", "guaracha edm", "hard house", "hardbag", "hardbass", "hip house",
            "house", "italo house", "jackin house", "jazz house", "jersey sound", "juke", "kwaito", "latin house",
            "lo-fi house", "melbourne bounce", "melodic house", "microhouse", "organic house", "outsider house",
            "phonk house", "progressive house", "rominimal", "slap house", "speed house", "stutter house", "tech house",
            "tribal house", "tropical house", "uk jackin", "vinahouse", "vocal house"),
        Sub(
            "Techno", ["Electronic"],
            "acholitronix", "acid techno", "acid trance", "ambient techno", "ambient trance", "balearic trance",
            "big room trance", "birmingham sound", "bleep techno", "dark psytrance", "deep techno", "demostyle",
            "detroit techno", "dream trance", "dub techno", "electro trance", "euro-trance", "forest psytrance",
            "free tekno", "full-on", "goa trance", "hard techno", "hard trance", "hardgroove techno", "hi-tech",
            "hi-tech full-on", "industrial techno", "makina", "melodic techno", "melodic trance", "minimal techno",
            "neo-trance", "night full-on", "nitzhonot", "peak time techno", "progressive psytrance",
            "progressive trance", "psycore", "psytrance", "schranz", "suomisaundi", "tbm", "tech trance", "techno",
            "trance", "trance 2.0", "vocal trance", "wonky techno", "zenonesque"),
        Sub(
            "Drum & Bass", ["Electronic"],
            "2-step", "acid breaks", "atmospheric drum and bass", "autonomic", "bassline", "big beat", "breakbeat",
            "breakbeat hardcore", "breakbeat kota", "breaks", "breakstep", "briddim", "brostep", "chillstep",
            "colour bass", "crossbreed", "dancefloor drum and bass", "dark garage", "darkcore", "darkstep", "deathstep",
            "deep drum and bass", "drill and bass", "drum and bass", "drumfunk", "drumstep", "dubstep", "dubwise",
            "dungeon sound", "florida breaks", "footwork jungle", "funky breaks", "future garage", "future riddim",
            "halftime", "hardcore breaks", "hardstep", "jazzstep", "jump up", "jungle", "jungle dutch", "jungle techno",
            "liquid funk", "liquid riddim", "melodic dubstep", "microfunk", "minimal drum and bass", "neurofunk",
            "nu skool breaks", "progressive breaks", "psybreaks", "purple sound", "ragga jungle", "riddim dubstep",
            "sambass", "skullstep", "speed garage", "tearout", "tearout brostep", "technoid", "techstep", "trancestep",
            "uk garage", "west coast breaks", "wonky"),
        Sub(
            "Ambient", ["Electronic"],
            "ambient", "ambient americana", "ambient dub", "black ambient", "dark ambient", "drone", "hauntology",
            "kankyō ongaku", "ritual ambient", "space ambient", "tribal ambient"),
        Sub("Downtempo", ["Electronic"], "chillout", "downtempo", "slowed & reverb", "trip hop"),
        Sub(
            "Industrial", ["Electronic"],
            "aggrotech", "cyber metal", "cyberpunk", "dark electro", "death industrial", "ebm", "electro-industrial",
            "epic collage", "hard beat", "industrial", "industrial hip hop", "industrial metal", "industrial rock",
            "machine rock", "martial industrial", "new beat", "post-industrial", "power noise", "skizzo", "steampunk"),
        Top(
            "Folk",
            "aboio", "appalachian folk", "bagad", "baguala", "biraha", "csárdás", "drinking song", "fahrtenlied",
            "falak", "festejo", "fife and drum blues", "fijiri", "folk", "haozi", "hungarian folk",
            "industrial folk song", "isa", "landó", "ländlermusik", "marinera", "marinera norteña", "military cadence",
            "música criolla", "neo-medieval folk", "nepali lok geet", "new mexico music", "néo-trad", "nóta",
            "old-time", "pagan folk", "pizzica", "polca criolla", "pásztordal", "rabbit song",
            "scottish country dance music", "scrumpy and western", "sea shanty", "seguidilla", "sevdalinka",
            "sevillanas", "shan'ge", "skiffle", "stornello", "sutartinės", "tajaraste", "talking blues", "tammurriata",
            "tarantella", "tonada asturiana", "trampská hudba", "trikitixa", "turkish folk", "vals criollo", "visa",
            "waulking song", "white voice", "work song", "xuc", "yodeling", "zeybek"),
        Sub(
            "Folk Rock", ["Folk", "Rock"],
            "british folk rock", "celtic rock", "folk rock", "medieval rock", "phleng phuea chiwit", "rock rural"),
        Sub(
            "Traditional Folk", ["Folk"],
            "cajun", "change ringing", "chicago polka", "contra", "eastern-style polka", "fife and drum",
            "football chant", "métis fiddling", "nouveau zydeco", "polka", "schottische", "zydeco"),
        Sub(
            "Contemporary Folk", ["Folk"],
            "alternative folk", "american primitive guitar", "anti-folk", "avant-folk", "campus folk", "chamber folk",
            "contemporary folk", "country folk", "dark folk", "filk", "folk pop", "freak folk", "free folk",
            "indie folk", "loner folk", "manele", "neofolk", "neofolklore", "progressive folk", "psychedelic folk",
            "stomp and holler", "tallava", "turbo-folk", "udigrudi", "wyrd folk"),
        Sub(
            "Celtic", ["Folk"],
            "cape breton fiddling", "celtic", "gaelic psalm singing", "hornpipe", "irish folk", "james bay fiddling",
            "kan ha diskan", "muiñeira", "pipe band music", "puirt à beul", "pìobaireachd", "sean-nós"),
        Top(
            "Latin",
            "avanzada", "bachata", "bachatón", "bambuco", "banda sinaloense", "bolero", "bolero español", "bolero son",
            "bomba", "bomba del chota", "boogaloo", "bulería", "bullerengue", "cachaca", "candombe", "candombe beat",
            "canto a lo poeta", "canto cardenche", "caporal", "carnavalito", "carranga", "chacarera", "chachachá",
            "chamamé", "chamamé tropical", "chamarrita rioplatense", "champeta", "changüí", "charanga", "chicha",
            "chilena", "chimayche", "chuscada", "conga", "copla", "copla cajamarquina", "corrido", "corrido tumbado",
            "cuarteto", "cubatón", "cueca", "cueca brava", "cumbia", "cumbia amazónica", "cumbia argentina",
            "cumbia chilena", "cumbia colombiana", "cumbia mexicana", "cumbia norteña mexicana",
            "cumbia norteña peruana", "cumbia peruana", "cumbia pop", "cumbia rebajada", "cumbia salvadoreña",
            "cumbia sanjuanera", "cumbia santafesina", "cumbia sonidera", "cumbia sureña peruana", "cumbia tejana",
            "cumbia turra", "cumbia villera", "cumbiatón", "cuplé", "currulao", "danzón", "dembow", "descarga",
            "doble paso", "fado", "fado de coimbra", "fandango", "fandango caiçara", "filin", "flamenco",
            "gaita zuliana", "guaguancó", "guajira", "guaracha", "guaracha santiagueña", "guarania", "habanera",
            "huapango", "huaylarsh", "huayno", "huayno con arpa", "jazz guachaca", "joropo", "jota", "jácara", "latin",
            "malagueña venezolana", "mambo", "mambo chileno", "mambo urbano", "mariachi", "merecumbé", "merengue",
            "merengue típico", "merenhouse", "merequetengue", "milonga", "morenada", "movimiento alterado", "muliza",
            "música llanera", "música típica chilena", "narcocorrido", "neoperreo", "norteño", "nueva cumbia chilena",
            "nueva trova", "nuevo flamenco", "nuevo tango", "onda nueva", "pachanga", "palo de mayo", "pandilla",
            "pasillo", "pasodoble", "payada", "pilón", "pirekua", "plena", "polka paraguaya", "porro", "punta", "punto",
            "ranchera", "reggaeton", "regional mexicano", "reparto", "rhumba", "rkt", "rumba", "rumba catalana",
            "rumba cubana", "rumba flamenca", "saeta", "salsa", "salsa choke", "salsa dura", "salsa romántica",
            "sanjuanito", "saya afroboliviana", "sierreño", "sikuri", "son calentano", "son cubano", "son de pascua",
            "son huasteco", "son istmeño", "son jarocho", "son montuno", "son nica", "songo", "tamborera", "tamborito",
            "tango", "taquirari", "technobanda", "tecnocumbia", "tecnomerengue", "tejano", "timba", "tonada potosina",
            "tonadilla", "tondero", "tropicanibalismo", "trova", "trova yucateca", "tumba francesa", "vallenato",
            "vals venezolano", "waila", "yaraví", "yu-mex", "zamacueca", "zamba"),
        Sub(
            "Brazilian", ["Latin"],
            "afoxê", "agronejo", "arrocha", "arrocha funk", "arrocha sertanejo", "arrochadeira", "baião", "bandinha",
            "batidão romântico", "batucada", "beat bolha", "beat bruxaria", "beat fino", "bossa nova",
            "brazilian phonk", "brega", "brega calypso", "brega funk", "cantoria", "carimbó", "choro", "ciranda",
            "coco", "cururu", "dobrado", "eletrofunk", "embolada", "forró", "forró de favela", "forró eletrônico",
            "forró universitário", "frevo", "frevo de bloco", "frevo de rua", "frevo elétrico", "frevo-canção",
            "funk automotivo", "funk brasileiro", "funk carioca", "funk consciente", "funk de bh", "funk mandelão",
            "funk melody", "funk ostentação", "funk proibidão", "funknejo", "guitarrada", "jongo", "lambada", "lundu",
            "maracatu", "marchinha", "maxixe", "mega funk", "moda de viola", "modinha", "mpb", "noiadance", "pagode",
            "pagode romântico", "pagodão", "partido alto", "piseiro", "rasqueado cuiabano", "rasteirinha", "repente",
            "ritmada", "samba", "samba de breque", "samba de gafieira", "samba de roda", "samba de terreiro",
            "samba soul", "samba-canção", "samba-choro", "samba-enredo", "samba-exaltação", "samba-joia", "samba-rock",
            "sambalanço", "seresta", "sertanejo", "sertanejo raiz", "sertanejo romântico", "sertanejo universitário",
            "tamborzão", "tecnobrega", "tecnofunk", "toada de boi", "trapfunk", "tropicália", "unakesa",
            "valsa brasileira", "vanera", "xaxado", "xote"),
        Sub("Latin Pop", ["Latin"], "grupera", "latin pop", "romantic flow", "tropipop"),
        Top(
            "Reggae",
            "dancehall", "deejay", "dub", "dub poetry", "flex dance music", "gommance", "gospel reggae", "jawaiian",
            "lovers rock", "mento", "novo dub", "pacific reggae", "ragga", "reggae", "reggae panameño", "reggae-pop",
            "rocksteady", "roots reggae", "samba-reggae", "seggae", "shatta", "skinhead reggae", "trap dancehall",
            "zess"),
        Sub("Ska", ["Reggae"], "2 tone", "jamaican ska", "ska", "third wave ska"),
        Top(
            "International",
            "aak", "abhang", "adhunik geet", "afro-zouk", "afrobeat", "afrobeats", "afropiano", "agbadza", "agbekor",
            "ahwash", "aita", "al jeel", "aleke", "algerian chaabi", "alloukou", "alté", "ambasse bey",
            "andalusian classical", "apala", "arabesk", "assiko", "axé", "baila", "baisha xiyue", "baithak gana",
            "balinese gamelan", "balitaw", "bandari", "banga", "bantengan", "bashment soca", "batuque", "baul gaan",
            "bedoui wahrani", "beijing opera", "bend-skin", "benga", "beni", "benna", "bhangra", "bhavageethe",
            "biguine", "bikutsi", "bocet", "boduberu", "boedra", "bogino duu", "bouyon", "bozlak", "burger-highlife",
            "burmese classical", "burmese mono", "burrakatha", "bélé", "ca trù", "cabo zouk", "cadence lypso",
            "cadence rampa", "calipso venezolano", "calypso", "campursari", "cante alentejano", "canto degli alpini",
            "cantonese opera", "cantu a chiterra", "cantu a tenore", "canzone d'autore", "canzone napoletana",
            "canzone neomelodica", "carnatic classical", "central asian throat singing", "chakacha", "chalga",
            "chamarrita açoriana", "changjak gugak", "chanson française", "chanson québécoise", "chanson réaliste",
            "chanson à texte", "chaozhou xianshi", "chimurenga", "chinese classical", "chinese literati music",
            "chinese opera", "chinese revolutionary opera", "chotis madrileño", "chutney", "chutney soca", "cilokaq",
            "coladeira", "compas", "congolese rumba", "cải lương", "dabke", "dangak", "dangdut", "danmono",
            "dennery segment", "desert blues", "desgarrada", "dhaanto", "dhol tasha", "dhrupad", "dikir barat",
            "dimotiko", "disco polo", "djanba", "dohori", "doina", "dondang sayang", "dongjing", "dongzu dage", "duma",
            "duranguense", "enka", "estrada", "fakaseasea", "famo", "fantezi", "filipino rondalla", "filmi",
            "finnish tango", "fon leb", "fuji", "funaná", "fungi", "fusion gugak", "gaana", "gagaku", "gagok",
            "gambang kromong", "gamelan", "gamelan angklung", "gamelan beleganjur", "gamelan degung", "gamelan gambang",
            "gamelan gender wayang", "gamelan gong gede", "gamelan gong kebyar", "gamelan jegog",
            "gamelan joged bumbung", "gamelan salendro", "gamelan sekaten", "gamelan selunding",
            "gamelan semar pegulingan", "gamelan siteran", "gamelan surakarta", "garba", "ghazal", "gnawa", "gondang",
            "goombay", "griot", "gstanzl", "guangdong yinyue", "guayla", "guggenmusik", "gumbe", "guoyue", "gwo ka",
            "għana", "haitian vodou drumming", "hambo", "hanmai", "harana", "harawi", "heikyoku", "henan opera",
            "highlife", "himene tarava", "hindustani classical", "honkyoku", "humppa", "hyangak", "hát tuồng",
            "indian classical", "iraqi maqam", "isicathamiya", "islamic modal music", "islamic religious music",
            "izlan", "izvorna bosanska muzika", "jaipongan", "japanese classical", "javanese gamelan", "jeongak",
            "jhumar", "jiangnan sizhu", "jibonmukhi gaan", "jit", "jiuta", "joik", "junkanoo", "jùjú", "jōruri",
            "kacapi suling", "kadodi", "kadongo kamu", "kafi", "kagura", "kai", "kalattut", "kalindula",
            "kalon'ny fahiny", "kaneka", "kantan chamorrita", "kanto", "kantruem", "kapuka", "kaseko", "kasékò",
            "kecak", "keroncong", "kete", "ketuk tilu", "khrueang sai", "khyal", "kidandali", "kidumbak", "kilapanga",
            "kizomba", "klapa", "klasik", "klezmer", "kliningan", "konnakol", "koplo", "korean classical",
            "korean revolutionary opera", "kouta", "krakowiak", "kuda lumping", "kujawiak", "kulintang", "kumi-daiko",
            "kumiuta", "kundiman", "kunqu", "kwassa kwassa", "kwela", "laiko", "langgam jawa", "lavani", "liscio",
            "liwa", "luk krung", "luk thung", "mahori", "mahraganat", "makossa", "malay gamelan", "malhun", "malouf",
            "maloya", "maloya électronique", "maloya élektrik", "mangambeu", "marrabenta", "maskanda", "mazurka",
            "mbalax", "mbolé", "mbube", "mchiriku", "meiji shinkyoku", "meyxana", "mgodro", "min'yō", "modern laiko",
            "mor lam", "mor lam sing", "morna", "moroccan chaabi", "moutya", "muak", "mugham", "mugithi", "musette",
            "muzika mizrahit", "muzika yehudit mekorit", "muzikat dika'on", "muziki wa dansi", "méringue",
            "mūsīqā lubnāniyya", "nagauta", "nanguan", "narodnozabavna glasba", "natya sangeet", "neo kyma", "ngoma",
            "nhạc tiền chiến", "nhạc vàng", "nhạc đỏ", "njuup", "noh", "oberek", "odissi classical", "ogene music",
            "omutibo", "ondō", "orkes gambus", "paghjella", "pala", "palm-wine", "pansori", "parang",
            "persian classical", "pimba", "pinpeat", "piosenka aktorska", "piphat", "polifonia occitana",
            "pon-chak disco", "pop raï", "powada", "power soca", "pungmul", "puxa", "qaraami", "qasidah modern",
            "quan họ", "quyi", "rabiz", "raqs baladi", "rara", "rasin", "raï", "rebetiko", "red song", "rigsar",
            "ripsaw", "rizitika", "runo song", "ryūkōka", "rōkyoku", "sa'idi", "salegy", "saluang klasik", "samri",
            "sanjo", "santé engagé", "sarala gee", "sardana", "sawt", "semba", "semi-trot", "seto leelo", "shaabi",
            "shanto", "shashmaqam", "shidaiqu", "shima-uta", "shinkyoku", "shōmyō", "sichuan opera", "sigidrigi",
            "sinawi", "sizhu music", "skiladiko", "slack-key guitar", "soca", "soukous", "southeast asian classical",
            "spouge", "staïfi", "steel band", "sufiana kalam", "sungura", "séga", "séga tambour", "sōkyoku", "taarab",
            "takamba", "talempong", "tanjidor", "tappa", "tarana", "tarawangsa", "tarraxinha", "tassa", "tassu",
            "tchinkoumé", "tembang cianjuran", "thai classical", "thumri", "timbila", "tivaner inngernerlu", "tizita",
            "township jive", "tradi-moderne congolais", "tradi-moderne ivoirien", "traditional séga", "trallalero",
            "trot", "tsapiky", "tsonga disco", "tsugaru-jamisen", "tumba", "tumbélé", "turkish classical", "twoubadou",
            "tân cổ giao duyên", "uaajeerneq", "unyago", "upopo", "urtiin duu", "urumi melam", "uyghur muqam",
            "uzun hava", "vaigat", "verbunkos", "vietnamese bolero", "vietnamese classical", "vude", "waka", "wangga",
            "wassoulou", "world fusion", "worldbeat", "xẩm", "yangzhou opera", "yanyue", "yayue", "yue opera", "yukar",
            "ziglibithy", "zinli", "zoblazo", "zohioliin duu", "zouglou", "zouk", "zouk love", "éntekhno",
            "özgün müzik", "čalgija", "ʻōteʻa"),
        Top("Vocal", "barbershop", "jubilee"),
        Sub("Traditional Pop", ["Vocal"], "hapa haole", "parlour music", "tin pan alley", "traditional pop"),
        Top(
            "Easy Listening",
            "cocktail nation", "easy listening", "exotica", "instrumental", "lounge", "sitarsploitation", "slow waltz",
            "space age pop", "whistling"),
        Top("Stage & Screen"),
        Sub(
            "Musicals", ["Stage & Screen"],
            "cabaret", "chèo", "industrial musical", "minstrelsy", "murga", "murga uruguaya", "music hall", "musical",
            "revue", "rock musical", "vaudeville"),
        Sub("Soundtracks", ["Stage & Screen"], "cinematic classical", "production music"),
        Top(
            "Religious",
            "adhan", "ashkenazi cantorial music", "baqashot", "beompae", "bhajan", "chazzanut", "chöd", "ginan",
            "kirtan", "maddahi", "maftirim", "mantra", "mappila", "mataali", "menzuma", "nagara naam", "nasheed",
            "nigun", "piyyut", "qawwali", "sacred harp", "shabad kirtan", "shoor", "stotra", "taoist ritual music",
            "tibetan buddhist chant", "vedic chant", "zemirot"),
        Sub(
            "Gospel", ["Religious"],
            "concert spiritual", "contemporary gospel", "country gospel", "gospel", "gospel hymn", "praise break",
            "ring shout", "sacred steel", "southern gospel", "spiritual art song", "spirituals",
            "traditional black gospel", "urban contemporary gospel"),
        Sub(
            "Contemporary Christian", ["Religious"],
            "christian metal", "christian rock", "contemporary christian", "praise & worship", "xian psych"),
        Top(
            "New Age",
            "andean new age", "asmr", "binaural beats", "broadband noise", "celtic new age", "native american new age",
            "neoclassical new age", "new age"),
        Top(
            "Avant-Garde",
            "ambient noise wall", "animal sounds", "avant-garde", "birdsong", "black noise", "conducted improvisation",
            "cut-up noise", "dark cabaret", "data sonification", "eai", "experimental", "field recording",
            "free improvisation", "harsh noise", "harsh noise wall", "lowercase", "mad", "nature sounds", "noise",
            "onkyo", "plunderphonics", "power electronics", "rain sounds", "reductionism", "sound art", "sound collage",
            "sound poetry", "spamwave", "tape music", "whale song", "ytpmv"),
        Top("Children's", "batonebi songs", "children's music", "iavnana", "lullaby"),
        Top("Holiday", "christmas music", "colindă"),
        Top(
            "Comedy/Spoken",
            "audio documentary", "audio drama", "beat poetry", "break-in", "comedy", "cowboy poetry", "fairy tale",
            "guided meditation", "interview", "jazz poetry", "kabarett", "kakawin", "lecture", "ngâm thơ", "non-music",
            "poetry", "prank calls", "punk poetry", "sermon", "sketch comedy", "slam poetry", "sound effects", "speech",
            "spoken word", "standup comedy"),
        Top("Other"),
    ];

    private static readonly Dictionary<string, Genre> ByName =
        Tree.ToDictionary(genre => genre.Name, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, Genre> ByMusicBrainz = Tree
        .SelectMany(genre => genre.MusicBrainz.Select(name => (Name: name, Genre: genre)))
        .ToDictionary(entry => entry.Name, entry => entry.Genre, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every genre, each top followed by its own subgenres.</summary>
    public static IReadOnlyList<string> All { get; } = [.. Tree.Select(genre => genre.Name)];

    /// <summary>The tops a genre sits under; empty for a top, or a name that is not one of these.</summary>
    public static IReadOnlyList<string> TopsOf(string genre) =>
        ByName.TryGetValue(genre, out var known) ? known.Tops : [];

    /// <summary>
    /// The genre a stated one folds into: a MusicBrainz genre, or one of these
    /// typed by a person; anything else is <see cref="Other"/>.
    /// </summary>
    public static string Of(string stated) =>
        ByMusicBrainz.TryGetValue(stated, out var genre) || ByName.TryGetValue(stated, out genre)
            ? genre.Name
            : Other;

    /// <summary>
    /// What a list of stated genres comes to: each folded, with its tops before
    /// it, once each, in the order they were stated.
    /// </summary>
    public static IReadOnlyList<string> Carried(IEnumerable<string> stated)
    {
        ArgumentNullException.ThrowIfNull(stated);

        var carried = new List<string>();

        foreach (var genre in stated.Select(Of))
        {
            foreach (var name in TopsOf(genre).Append(genre))
            {
                if (!carried.Contains(name, StringComparer.Ordinal)) carried.Add(name);
            }
        }

        return carried;
    }

    /// <summary>The stated names, of those given, that carry a genre the test accepts.</summary>
    /// <remarks>
    /// How a question about these genres is put to stored MusicBrainz ones in
    /// SQL: the names the library states are few, so each is asked here.
    /// </remarks>
    public static IReadOnlyList<string> Stating(IEnumerable<string> stated, Func<string, bool> carries)
    {
        ArgumentNullException.ThrowIfNull(stated);
        ArgumentNullException.ThrowIfNull(carries);

        return [.. stated.Where(name => Carried([name]).Any(carries))];
    }
}
