===============================================================================
  TeknoParrot Manager - HyperSpin 2 Plugin  |  v0.13.0
  Author: Jumpstile
===============================================================================

  A HyperSpin 2 / HyperHQ plugin that sets up and manages your TeknoParrot
  arcade games -- registration, control setup, crosshairs, and more, done
  automatically instead of game by game.

  For a one-page version, see QUICKSTART.txt.


-------------------------------------------------------------------------------
  WHO IS THIS FOR?
-------------------------------------------------------------------------------

  This plugin is for HyperSpin 2 users who already have TeknoParrot
  installed and want their TeknoParrot games registered, controlled, and
  added to their library automatically instead of by hand.

  You will get the most out of it if you:

    -- have several TeknoParrot games and don't want to register or bind
       controls for each one individually
    -- use lightgun games and want crosshairs and cursor-hiding set up
       across all of them at once
    -- want your TeknoParrot games to appear in HyperHQ alongside the rest
       of your library

  You may not need this plugin if you only have a handful of games and
  prefer to set everything up by hand in TeknoParrotUI.


-------------------------------------------------------------------------------
  WHAT IT DOES
-------------------------------------------------------------------------------

  - Health check. Checks your TeknoParrot folder structure and reports how
    many of your games are working, missing a profile, or have a broken
    game path.

  - Automatic registration. Scans your TeknoParrot GameProfiles and folders,
    matches each game to the correct profile, and creates the missing user
    profile so it appears and launches in TeknoParrot -- and, once synced,
    in HyperHQ too. Existing profiles are never overwritten.

  - Fuzzy and dat-assisted matching. For games that share one executable
    across many titles, folder names are compared to candidate profile
    codes and the best match is used when the confidence is high enough.
    An optional, but highly recommended, "Collection Dat" file can resolve
    a lot of games this plugin otherwise couldn't tell apart. The plugin
    can check for and download the latest one for you ("Check Collection
    Dat For Updates" / "Download Collection Dat") -- it's only ever read,
    never run or installed. This file is community-maintained by Eggman
    -- see CREDITS below.

  - Game path repair. Finds broken or empty game paths and repoints them
    to the right executable, but only when there is exactly one possible
    match -- ambiguous cases are left for you to fix by hand.

  - Control setup (propagation). Bind the controls for ONE game of a given
    type (driving, lightgun, trackball, joystick, or button games) -- that
    becomes your "reference game" for that type. The plugin copies that
    setup to every other unbound game of the same type, matched by button
    function so a steering value never lands on a gun trigger. Your
    reference game's own bindings are never changed.

  - Device survey. Tells you which controller, lightgun, or wheel to use
    for each game type, based on what you have plugged in. Read-only --
    it doesn't change anything.

  - Crosshair setup. Deploys a chosen Player 1 / Player 2 crosshair image
    pair to every registered lightgun game. 321 ready-made designs are
    included, and you can browse them all in a preview page before
    picking. Can also hide the Windows mouse cursor during lightgun play.

  - GPU compatibility fix. Detects your graphics card (AMD, NVIDIA, or
    Intel) and automatically applies the matching fix setting to every
    game that supports one. Works entirely on your own computer -- no
    internet connection used or needed.

  - ReShade setup. Installs ReShade (a free tool for sharper image,
    CRT-style scanlines, richer colors, and more) into your games. You
    supply the ReShade file yourself -- this plugin never downloads it.
    Figures out the right way to install it for each game automatically.

  - dgVoodoo2 setup. Fixes older games that crash or show a black screen
    on modern PCs. You supply the files yourself -- this plugin never
    downloads them. Automatically figures out which games need it and
    installs only what each one needs.

  - BepInEx update check. If a game already has BepInEx (a popular
    modding framework) installed, checks for and installs a newer version
    straight from BepInEx's own official GitHub page. Never installs
    BepInEx for the first time -- only updates an existing install. Your
    existing files are backed up first.

  - Force feedback setup. Two separate ways to get vibration/rumble
    working: TeknoParrot's own "FFB Blaster" (needs a paid TeknoParrot
    membership to actually work), and a free third-party plugin covering
    a different set of games, downloaded straight from its official
    GitHub page. If a game is covered by both, FFB Blaster is preferred
    by default.

  - Backup and restore. Backs up your game profiles before any risky
    change, and lets you restore an earlier backup -- with a safety
    backup of your current profiles taken automatically first.

  - HyperHQ sync. Adds an "Arcade (TeknoParrot)" section to your HyperHQ
    library and keeps it in sync with your registered TeknoParrot games.

  - Setup wizard. A guided first-run wizard walks you through pointing the
    plugin at your TeknoParrot folder and configuring everything above,
    in plain language.


-------------------------------------------------------------------------------
  WHAT IT WILL NOT DO
-------------------------------------------------------------------------------

  - It will not install TeknoParrot itself -- you need that installed
    first.
  - It will not download game files, ROMs, or any copyrighted content.
  - It will not guess when it isn't sure. If something is ambiguous, it
    skips that item and tells you, instead of making a risky guess.
  - It always lets you preview an action before committing to it.


-------------------------------------------------------------------------------
  REQUIREMENTS
-------------------------------------------------------------------------------

  - HyperSpin 2 / HyperHQ installed and running.
  - TeknoParrot installed, with TeknoParrotUi.exe run at least once so it
    has downloaded its GameProfiles folder.


-------------------------------------------------------------------------------
  INSTALLING THE PLUGIN
-------------------------------------------------------------------------------

  Step 1.  Download the plugin ZIP from the Releases page.

  Step 2.  Extract it into its own folder inside your HyperHQ Plugins
           folder -- it should end up as
           Plugins\TeknoParrotManagerHyperSpin2Plugin\(files here), not
           loose inside the Plugins folder itself.

  Step 3.  Restart HyperHQ (or reload plugins, if your HyperHQ build has
           that option).

  Step 4.  Open the plugin's page in HyperHQ and run the setup wizard.

  Full step-by-step instructions: QUICKSTART.txt.


-------------------------------------------------------------------------------
  CONTROL SETUP (PROPAGATION)
-------------------------------------------------------------------------------

  Binding controls for every game by hand is tedious when many games use
  the same device. Instead, bind ONE game of each control type, and the
  plugin copies those controls to the rest.

  How to use it:

    1. In TeknoParrotUi.exe, fully bind one game of each control type you
       use -- a lightgun game, a driving game, a fighting game, a
       trackball game, and so on. This game becomes that type's
       "reference game".

    2. In the plugin, run "Device Survey" first if you want a
       recommendation on which device to use for which game type.

    3. Click "Preview Control Setup" to see what would be copied and from
       which reference game, then "Set Up Controls" to apply it.

  What is safe:

    - Your reference games are read and never modified by this step.
    - A game you have already bound is detected and left unchanged.
    - Everything is reported so you can see exactly what changed.

  An optional control-overrides JSON file (the controlOverridesPath
  setting) lets you exclude a game from this copying entirely, pin it to
  a specific reference game, override its detected control type, or -- if
  two of your reference games for the same type disagree on their Input
  API setting -- tell the plugin which one is correct so the other one
  gets fixed to match (the canonicalArchetype setting). Your actual
  button bindings are never touched by canonicalArchetype -- only that
  one Input API field, and only when you've said explicitly which
  reference game is right.

  After running control setup, launch one updated game and test it
  before trusting the rest.


-------------------------------------------------------------------------------
  CROSSHAIR SETUP
-------------------------------------------------------------------------------

  Deploys custom Player 1 / Player 2 crosshair images to every registered
  lightgun game (House of the Dead 4, Aliens Extermination, and similar
  titles).

  How to use it:

    1. Click "Preview Crosshairs" to open a browsable preview page showing
       all 321 included designs (or your own, if you've pointed the
       crosshairsPath setting at a different folder).

    2. Pick a Player 1 design and a Player 2 design.

    3. Click "Deploy Crosshairs" to copy them to every registered lightgun
       game. ElfLdr2 and PCSX2 games are handled as special cases since
       they share one emulator folder between multiple games.

    4. Optionally click "Hide Cursor" to hide the Windows mouse pointer
       during lightgun play.

  Run this again any time to change your crosshair design.


-------------------------------------------------------------------------------
  GPU COMPATIBILITY FIX
-------------------------------------------------------------------------------

  Some games run better, or only run correctly, with a setting matched to
  your specific graphics card brand. This automatically finds and applies
  that setting for every game that has one.

  How to use it:

    1. Click "Preview GPU Fix" to see which games would be changed,
       without changing anything yet. The plugin detects your graphics
       card automatically.
    2. Click "Apply GPU Fix" to apply it. Everything is backed up first.

  If the plugin can't detect your graphics card automatically (uncommon),
  you can tell it which one you have when running the action.

  Safe to run again any time you change or update your graphics card.
  This feature never uses the internet -- everything happens on your own
  computer.


-------------------------------------------------------------------------------
  RESHADE SETUP
-------------------------------------------------------------------------------

  ReShade is a free, well-known tool that can make your games look
  better -- sharper image, CRT-style scanlines, richer colors, decorative
  borders, and more. It doesn't change your actual game files, and can be
  removed at any time by deleting one file.

  This plugin does NOT download ReShade for you. You need to get it
  yourself first:

    1. Go to https://reshade.me and download the installer.
    2. Run it, and when asked, point it at any 64-bit game's .exe -- it
       will create a DLL file in that game's folder.
    3. Set that DLL as the "ReShade DLL, 64-bit" setting in this plugin.
       (Optional: repeat with a 32-bit game if you have any, and set it
       as "ReShade DLL, 32-bit".)

  How to use it:

    1. Click "Check ReShade For Updates" to confirm your file is genuine
       and see if a newer version is available. Doesn't change anything.
    2. Click "Preview ReShade Setup" to see which games would be changed.
    3. Click "Apply ReShade Setup" to install it. Everything is backed up
       first, and your game files themselves are never modified.

  Once installed, launch a game and press the Home key to turn effects on
  or off. To remove ReShade from a game, delete the one file it added to
  that game's folder -- nothing else is touched.

  The plugin automatically figures out which version of ReShade each game
  needs (based on the game's graphics technology) and installs the right
  one -- you don't need to know any of these details yourself.


-------------------------------------------------------------------------------
  DGVOODOO2 SETUP
-------------------------------------------------------------------------------

  Some older arcade games crash or show a black screen on modern PCs and
  graphics cards, because of how they were originally written to talk to
  the hardware. dgVoodoo2 is a free tool that fixes this by translating
  those old calls into something your modern graphics card understands.
  It doesn't change your actual game files, and can be removed at any
  time by deleting the files it added.

  This plugin does NOT download dgVoodoo2 for you. You need to get it
  yourself first:

    1. Go to https://dege.freeweb.hu/dgVoodoo2/dgVoodoo2/ and download it.
    2. Put the files somewhere on your computer.
    3. Set that folder as the "dgVoodoo2 Folder" setting in this plugin.

  How to use it:

    1. Click "Preview dgVoodoo2 Setup" to see which games would be fixed.
       The plugin automatically figures out which of your games need it.
    2. Click "Apply dgVoodoo2 Setup" to install it. Everything is backed
       up first, and your game files themselves are never modified.

  By default, only games that actually need this are touched -- games
  that already work fine are left alone. To uninstall, delete the files
  it added to a game's folder; nothing else is changed.


-------------------------------------------------------------------------------
  BEPINEX UPDATE CHECK
-------------------------------------------------------------------------------

  BepInEx is a popular modding framework some arcade game ports rely on.
  If a game already has it installed, this plugin can check for and
  install a newer version for you.

  This is one of two features in this plugin that downloads something
  automatically -- but only an UPDATE. It never installs BepInEx for the
  first time; a game without BepInEx already installed is left
  completely alone, no matter what.

  How to use it:

    1. Click "Check BepInEx For Updates" to see which of your games with
       BepInEx already installed have a newer version available.
    2. Click "Update BepInEx" to install it. Each game's existing BepInEx
       files are backed up first.

  Where it downloads from: BepInEx's own official GitHub Releases page --
  never a third-party mirror. The download is checked against that
  official source before it's ever fetched, and the file extracted from
  it is checked to make sure nothing in it can write outside the game's
  own folder.

  Games with a 32-bit BepInEx install, or no BepInEx install at all, are
  left untouched.


-------------------------------------------------------------------------------
  FORCE FEEDBACK SETUP
-------------------------------------------------------------------------------

  There are two separate ways to get force feedback (vibration/rumble)
  working in TeknoParrot, covering different games:

  FFB BLASTER (TeknoParrot's own built-in force feedback)

    This only works with an active, paid TeknoParrot membership
    (teknoparrot.com/en/Home/Subscription) -- this plugin has no way to
    check whether you have one, so turning this on has zero effect if you
    don't. No files are downloaded for this -- it's a local setting only.

    1. Click "Preview FFB Blaster Setup" to see which games would be
       affected.
    2. Click "Apply FFB Blaster Setup" to turn it on for every supported
       game.

  FFB PLUGIN (a free, open-source alternative)

    Covers a different set of games than FFB Blaster. This is the second
    feature in this plugin that downloads something automatically --
    straight from the plugin's own official GitHub page, never a
    third-party mirror.

    1. Click "Preview FFB Plugin Setup" to see which games would be
       affected.
    2. Click "Apply FFB Plugin Setup" to install it.

    A game already covered by FFB Blaster is left alone by default (FFB
    Blaster is preferred) -- existing files at the destination are never
    overwritten either way.

  To uninstall either one: for FFB Blaster, there's no file to delete --
  just run TeknoParrotUI and turn the setting back off. For the FFB
  Plugin, delete the one DLL file it added to the game's folder.


-------------------------------------------------------------------------------
  BACKING UP AND RESTORING
-------------------------------------------------------------------------------

  - Click "Backup Profiles" at any time to save a copy of your current
    TeknoParrot game profiles.
  - Click "Restore Backup" to bring back an earlier backup. The plugin
    automatically backs up your current profiles first, just in case.
  - A backup is also taken automatically before any other action that
    changes profile files.


-------------------------------------------------------------------------------
  RELATIONSHIP TO TEKNOPARROT MANAGER
-------------------------------------------------------------------------------

  TeknoParrot Manager is a broader PowerShell tool by the same author that
  also handles Windows-specific setup tasks (ReShade, dgVoodoo2, GPU
  fixes, force feedback, BepInEx updates, PostgreSQL setup, and more) for
  LaunchBox, RetroBat, and Batocera as well as HyperSpin 2.

  This plugin intentionally keeps a narrower scope, focused on what makes
  sense as a HyperHQ plugin:

    Included:     profile discovery, missing profile registration (with
                  dat-index and profile-code fuzzy fallback), unique path
                  repair, control setup, device survey, crosshair
                  deployment, cursor-hide setup, health reporting,
                  backups, HyperHQ library sync, GPU compatibility fixes,
                  ReShade setup, dgVoodoo2 setup, BepInEx update
                  checking, and force feedback setup.

    Not yet:      PostgreSQL setup. This is planned -- see the project's
                  ROADMAP for progress.

  HyperHQ remains your launcher and library manager; this plugin exists to
  give it the structured TeknoParrot profile and import behavior it needs.


-------------------------------------------------------------------------------
  SAFETY
-------------------------------------------------------------------------------

  - Registration and repair support a dry-run preview before writing any
    profile file.
  - Existing user profiles are never overwritten by registration.
  - Game path repair only writes when there is a single, unambiguous
    match.
  - Restoring a backup automatically backs up your current profiles first.
  - ReShade and dgVoodoo2 are installed from files you already supplied --
    this plugin never downloads either tool itself.
  - BepInEx update checking is the first exception: it downloads BepInEx's
    own official release from BepInEx's own official GitHub page, and
    only ever as an update to a game that already has BepInEx installed.
    See the BEPINEX UPDATE CHECK section above for the full safeguards.
  - The FFB Plugin (free force feedback) is the second exception: it
    downloads two small DLLs and a compatibility list from its own
    official GitHub page. FFB Blaster (TeknoParrot's own force feedback)
    downloads nothing -- it only has an effect with a paid TeknoParrot
    membership, which this plugin can't check, so it tells you so plainly
    before you click it. See the FORCE FEEDBACK SETUP section above.
  - Besides HyperHQ's own communication channel, the plugin makes five
    kinds of outbound network calls, all read-only or explicitly
    triggered by you: a check of the public TeknoParrotUI profile-code
    list (falls back to your local GameProfiles listing without error if
    it fails); the optional collection dat check/download described
    above, which only runs when you click "Check Collection Dat For
    Updates" or "Download Collection Dat"; the optional ReShade version
    check; the optional BepInEx update check/download described above;
    and the optional FFB Plugin table/DLL fetch described above.


-------------------------------------------------------------------------------
  GOOD TO KNOW
-------------------------------------------------------------------------------

  - Every action that changes files can be previewed first.
  - If the plugin isn't sure about a match, it skips that item and tells
    you about it rather than guessing.
  - See CHANGELOG.txt for what changed in this version.
  - See QUICKSTART.txt for a step-by-step setup walkthrough.


-------------------------------------------------------------------------------
  CREDITS
-------------------------------------------------------------------------------

  - The Collection Dat file (used to help recognize games during setup,
    and downloadable directly from this plugin) is community-maintained
    by Eggman:  https://github.com/Eggmansworld/TeknoParrot
    This plugin does not create or maintain that data -- it only
    downloads and reads it.

  - ReShade is developed by crosire and distributed under the BSD
    3-Clause license:  https://reshade.me  /  https://github.com/crosire/reshade
    This plugin does not develop, distribute, or download ReShade -- you
    get it directly from the official site, and this plugin only installs
    the file you already downloaded.

  - dgVoodoo2 is developed by Dege:  https://dege.freeweb.hu/dgVoodoo2/dgVoodoo2/
    This plugin does not develop, distribute, or download dgVoodoo2 -- you
    get it directly from the official site, and this plugin only installs
    the files you already downloaded.

  - BepInEx is developed by the BepInEx team and distributed under the
    LGPL-2.1 license:  https://github.com/BepInEx/BepInEx
    This plugin downloads BepInEx updates directly from its official
    GitHub Releases page, and only as an update to an already-existing
    install -- it never installs BepInEx for the first time.

  - The FFB Arcade Plugin is developed by mightymikem and distributed
    under the GPL-3.0 license:  https://github.com/mightymikem/FFBArcadePlugin
    This plugin downloads it directly from that official GitHub repo.
    FFB Blaster is TeknoParrot's own built-in feature, not a separate
    project -- nothing is downloaded for it.


-------------------------------------------------------------------------------
  SUPPORT THIS PROJECT
-------------------------------------------------------------------------------

  This plugin is free to use. If it's been useful to you and you'd like
  to support continued development, you can buy the author a coffee:

    https://buymeacoffee.com/jumpstile

  Completely optional -- never required to use any feature of this plugin.


===============================================================================
  Quick start: QUICKSTART.txt   |   What changed: CHANGELOG.txt
===============================================================================
