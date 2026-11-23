# -*- coding: utf-8 -*-
"""Generate the built-in vehicle catalog from an existing local Data_Car_c0.tsv.

Usage:
    python tools/generate_car_catalog.py path/to/offline-tsv [output-cs]
"""
import argparse
import csv
import os
import re

PROJECT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_OUT = os.path.join(PROJECT, "Catalog", "VehicleCatalogData.g.cs")
REVIEW_OUT = os.path.join(PROJECT, ".cache", "catalog-review.txt")

# MakeID -> (Chinese, English). The database stores localized name hashes, so readable names
# are curated here. English follows the names Forza uses in the car list.
MAKES = {
    1: ("讴歌", "Acura"), 2: ("阿斯顿·马丁", "Aston Martin"), 3: ("奥迪", "Audi"),
    4: ("宾利", "Bentley"), 6: ("宝马", "BMW"), 7: ("别克", "Buick"), 8: ("凯迪拉克", "Cadillac"),
    9: ("雪佛兰", "Chevrolet"), 11: ("道奇", "Dodge"), 13: ("法拉利", "Ferrari"),
    14: ("福特", "Ford"), 16: ("本田", "Honda"), 17: ("现代", "Hyundai"), 19: ("捷豹", "Jaguar"),
    20: ("柯尼塞格", "Koenigsegg"), 21: ("兰博基尼", "Lamborghini"), 22: ("蓝旗亚", "Lancia"),
    23: ("雷克萨斯", "Lexus"), 24: ("路特斯", "Lotus"), 25: ("玛莎拉蒂", "Maserati"),
    26: ("马自达", "Mazda"), 27: ("迈凯伦", "McLaren"), 28: ("梅赛德斯-奔驰", "Mercedes-Benz"),
    29: ("MINI", "MINI"), 30: ("三菱", "Mitsubishi"), 31: ("日产", "Nissan"), 32: ("欧宝", "Opel"),
    34: ("帕加尼", "Pagani"), 36: ("标致", "Peugeot"), 38: ("庞蒂亚克", "Pontiac"),
    39: ("保时捷", "Porsche"), 41: ("雷诺", "Renault"), 43: ("赛麟", "Saleen"),
    47: ("谢尔比", "Shelby"), 48: ("斯巴鲁", "Subaru"), 49: ("丰田", "Toyota"), 50: ("TVR", "TVR"),
    51: ("沃克斯豪尔", "Vauxhall"), 52: ("大众", "Volkswagen"), 53: ("沃尔沃", "Volvo"),
    54: ("阿尔法·罗密欧", "Alfa Romeo"), 55: ("路虎", "Land Rover"), 56: ("雪铁龙", "Citroën"),
    57: ("菲亚特", "Fiat"), 59: ("霍顿", "Holden"), 66: ("Radical", "Radical"),
    72: ("Noble", "Noble"), 77: ("德·托马索", "De Tomaso"), 78: ("德罗宁", "DeLorean"),
    80: ("GMC", "GMC"), 86: ("AMG", "AMG"), 88: ("吉普", "Jeep"), 89: ("普利茅斯", "Plymouth"),
    90: ("阿巴斯", "Abarth"), 96: ("Ultima", "Ultima"), 97: ("奥斯汀·希利", "Austin-Healey"),
    98: ("MG", "MG"), 102: ("轩尼诗", "Hennessey"), 105: ("林肯", "Lincoln"),
    107: ("Ariel", "Ariel"), 108: ("KTM", "KTM"), 111: ("BAC", "BAC"), 123: ("公羊", "RAM"),
    124: ("达特桑", "Datsun"), 126: ("SRT", "SRT"), 133: ("Zenvo", "Zenvo"),
    137: ("梅赛德斯-AMG", "Mercedes-AMG"), 138: ("HSV", "HSV"), 139: ("Meyers", "Meyers"),
    140: ("Penhall", "Penhall"), 141: ("Polaris", "Polaris"), 142: ("AC", "AC"),
    146: ("Reliant", "Reliant"), 151: ("RJ Anderson", "RJ Anderson"), 161: ("Exomotive", "Exomotive"),
    162: ("Can-Am", "Can-Am"), 166: ("Funco", "Funco"), 171: ("Peel", "Peel"),
    172: ("Formula Drift", "Formula Drift"), 180: ("Sunbeam", "Sunbeam"), 183: ("里马克", "Rimac"),
    184: ("阿波罗", "Apollo"), 200: ("Playground Games", "Playground Games"),
    203: ("Ginetta", "Ginetta"), 206: ("DeBerti", "DeBerti"), 213: ("Sierra Cars", "Sierra Cars"),
    215: ("五菱", "Wuling"), 218: ("戈登·默里", "Gordon Murray"), 220: ("Jimco", "Jimco"),
    221: ("里维安", "Rivian"), 223: ("CCM", "CCM"), 226: ("舒潘", "Schuppan"),
    233: ("路西德", "Lucid"), 237: ("Eunos", "Eunos"), 239: ("Autozam", "Autozam"),
    262: ("Gazoo Racing", "Gazoo Racing"),
}

COUNTRIES = {
    1: ("澳大利亚", "Australia"), 2: ("法国", "France"), 3: ("德国", "Germany"),
    4: ("意大利", "Italy"), 5: ("日本", "Japan"), 6: ("韩国", "Korea"), 7: ("西班牙", "Spain"),
    8: ("瑞典", "Sweden"), 9: ("英国", "United Kingdom"), 10: ("美国", "United States"),
    11: ("摩纳哥", "Monaco"), 12: ("荷兰", "Netherlands"), 13: ("葡萄牙", "Portugal"),
    14: ("苏格兰", "Scotland"), 15: ("南非", "South Africa"), 16: ("奥地利", "Austria"),
    17: ("比利时", "Belgium"), 18: ("丹麦", "Denmark"), 20: ("墨西哥", "Mexico"),
    21: ("捷克", "Czech Republic"), 22: ("新西兰", "New Zealand"), 23: ("瑞士", "Switzerland"),
    24: ("阿联酋", "United Arab Emirates"), 25: ("巴西", "Brazil"), 26: ("加拿大", "Canada"),
    27: ("美国", "United States"), 28: ("印度", "India"), 29: ("圣卢西亚", "Saint Lucia"),
    30: ("克罗地亚", "Croatia"), 31: ("中国", "China"),
}

ASPIRATIONS = {
    1: "自然吸气", 2: "涡轮增压", 3: "双涡轮增压", 4: "四涡轮增压",
    5: "机械增压（DSC）", 6: "机械增压（CSC）", 7: "涡轮+机械双增压", 8: "无增压",
}

ENGINE_CONFIGS = {
    1: "V 型", 2: "W 型", 3: "直列", 4: "转子", 5: "水平对置",
    6: "电动", 7: "混动（SH）", 8: "混动（PH）", 9: "混动（SPH）",
}

# CylinderID -> (气缸数, 转子数)
CYLINDERS = {
    1: (4, 0), 2: (5, 0), 3: (6, 0), 4: (8, 0), 5: (10, 0), 6: (12, 0), 7: (16, 0),
    8: (2, 1), 9: (3, 1), 10: (0, 0), 11: (3, 0), 12: (4, 1), 13: (2, 0), 14: (1, 0),
}

# DB DriveTypeID 1=FWD 2=RWD 3=AWD (the Data Out drivetrain field is 0/1/2 — keep separate).
DRIVES = {1: ("前驱", "FWD"), 2: ("后驱", "RWD"), 3: ("四驱", "AWD")}

# Exact model tokens the generic splitter cannot handle (acronyms, special spellings).
TOKEN_OVERRIDES = {
    "250GTO": "250 GTO", "250GTLusso": "250 GT Lusso", "275GTS": "275 GTS", "512S": "512 S",
    "512TR": "512 TR", "330P4": "330 P4", "512S": "512 S", "599XX": "599XX",
    "599XXEvolution": "599XX Evolution", "F142": "458 Italia", "F12tdf": "F12tdf",
    "790B": "787B", "NSXRGT": "NSX-R GT", "XKRSGT": "XKR-S GT", "GTSACR": "GTS ACR",
    "Z4MCoupe": "Z4 M Coupé", "850CSi": "850 CSi", "WRXSTiS209": "WRX STI S209",
    "STi": "STI", "WRXSTI": "WRX STI", "WRXSTi": "WRX STI", "WRXS": "WRX S",
    "GiuliaGTAm": "Giulia GTAm", "RCF": "RC F", "GTSR": "GTS-R", "GTIR": "GTI-R",
    "SkylineGTR": "Skyline GT-R", "SkylineGTRVSpecII": "Skyline GT-R V-Spec II",
    "SkylineGTSR": "Skyline GTS-R", "Skyline2000GTR": "Skyline 2000GT-R",
    "GTRVSpecII": "GT-R V-Spec II", "240RSWRC": "240 RS", "TSRS": "TSR-S",
    "CX75": "C-X75", "Etron": "e-tron", "XClass": "X-Class", "XJR15": "XJR-15",
    "GTAm": "GTAm", "ClubSport": "ClubSport", "LancerEvoIII": "Lancer Evolution III",
    "LancerEvoVITME": "Lancer Evolution VI TME", "LancerEvoVIIIMR": "Lancer Evolution VIII MR",
    "LancerEvoVIIIMRWP": "Lancer Evolution VIII MR WP",
    "LancerEvoIXMR": "Lancer Evolution IX MR", "LancerEvoXGSR": "Lancer Evolution X GSR",
    "E36M3": "M3 E36", "E46M3": "M3 E46", "M3E92": "M3 E92", "M3GTS": "M3 GTS",
    "M5E28": "M5 E28", "M5E39": "M5 E39", "M2Comp": "M2 Competition", "M4CS": "M4 CS",
    "M4GTS": "M4 GTS", "M4WP": "M4 WP", "M2FE": "M2 FE", "GolfGTi16vMk2": "Golf GTi 16v Mk2",
    "Supra20GT": "Supra 2.0 GT", "SupraGR": "GR Supra", "R390": "R390 GT1",
    "240SX": "240SX", "240ZRallye": "240Z Rallye", "205T16": "205 T16", "205Rallye": "205 Rallye",
    "650SSpider": "650S Spider", "600LT": "600LT", "765LT": "765LT", "620R": "620R",
    "370ZNismo": "370Z Nismo", "190EFE": "190 E FE", "ZNismo": "Z Nismo",
    "SVTLightning": "SVT Lightning", "LP7004": "LP 700-4", "LP6704SV": "LP 670-4 SV",
    "CTSV": "CTS-V", "CTSV Sedan": "CTS-V Sedan",
    "CTSVSedan": "CTS-V Sedan", "CT4VBlackwing": "CT4-V Blackwing", "CT5VBlackwing": "CT5-V Blackwing",
    "ATSV": "ATS-V", "CorvetteERay": "Corvette E-Ray", "MX5RF": "MX-5 RF", "MX5Cup": "MX-5 Cup",
    "MX5": "MX-5", "RX7": "RX-7", "RX8": "RX-8", "RX3": "RX-3", "RX3FE": "RX-3 FE",
    "SCargo": "S-Cargo", "SCargoFE": "S-Cargo FE", "CivicR": "Civic Type R",
    "CivicRS": "Civic RS", "CivicSi": "Civic Si", "NSXR": "NSX-R", "NSXTypeS": "NSX Type S",
    "IntegraR": "Integra Type R", "RSXTypeS": "RSX Type S", "SilviaKs": "Silvia K's",
    "Fairlady432Z": "Fairlady Z 432", "FairladyZvSTwinTurbo": "Fairlady Z Version S Twin Turbo",
    "BE1": "BE-1", "AZ1": "AZ-1", "2CV": "2CV", "2000GT": "2000GT", "GT40": "GT40",
    "Etype": "E-Type", "EtypeLightweight": "E-Type Lightweight", "DType": "D-Type",
    "DMC12": "DMC-12", "Dino246": "Dino 246", "S800": "S800", "S660": "S660",
    "i8": "i8", "iX": "iX", "i20N": "i20 N", "i30N": "i30 N", "IDBuzz": "ID. Buzz",
    "F150": "F-150", "F150FE": "F-150 FE", "F150Lightning": "F-150 Lightning",
    "F150RaptorR": "F-150 Raptor R", "F150RaptorRWP": "F-150 Raptor R WP",
    "F150TrophyTruck": "F-150 Trophy Truck", "F250Deberti": "F-250 DeBerti",
    "F450DRW": "F-450 DRW", "F450DRWFE": "F-450 DRW FE", "FXXK": "FXX-K",
    "FXXKEvo": "FXX-K Evo", "FXXKEvoWP": "FXX-K Evo WP", "F1GT": "F1 GTR",
    "EvoTA": "Evo TA", "Evo": "Evo", "GolfGTI": "Golf GTI", "G63AMG6x6": "G63 AMG 6x6",
    "A45AMG": "A 45 AMG", "C63AMG": "C 63 AMG", "E63SAMG": "E 63 S AMG",
    "SLC43AMG": "SLC 43 AMG", "SL63AMG": "SL 63 AMG", "SL65AMG": "SL 65 AMG",
    "C63AMGCoupe": "C 63 Coupé", "AMGGT": "AMG GT", "AMGGTR": "AMG GT R",
    "AMGGT4": "AMG GT 4-Door", "AMGOne": "AMG One", "AMGHammer": "AMG Hammer",
    "GTBlackSeries": "GT Black Series", "GTBlackSeriesWP": "GT Black Series WP",
    "MegaTrack": "Mega Track", "HuracanEVO": "Huracán EVO",
    "HuracanEVOSpyder": "Huracán EVO Spyder", "HuracanSTO": "Huracán STO",
    "HuracanSterrato": "Huracán Sterrato", "HuracanTecnica": "Huracán Tecnica",
    "GallardoLP570Spyder": "Gallardo LP 570-4 Spyder", "AventadorSVJ": "Aventador SVJ",
    "AventadorUltimae": "Aventador Ultimae", "MP412C": "MP4-12C", "MC12Corsa": "MC12 Corsa",
    "399XX": "599XX", "718CaymanGTS": "718 Cayman GTS", "718CaymanGT4RS": "718 Cayman GT4 RS",
    "911GT2": "911 GT2", "911GT2ID": "911 GT2 ID", "911GT3": "911 GT3",
    "911GT3RS": "911 GT3 RS", "911Turbo": "911 Turbo", "911TurboS": "911 Turbo S",
    "911Dakar": "911 Dakar", "911LuftAuto": "911 LuftAuto", "996GT1": "996 GT1",
    "996GT3": "996 GT3", "918Spyder": "918 Spyder", "928GTS": "928 GTS", "944Turbo": "944 Turbo",
    "968TurboS": "968 Turbo S", "962CRS": "962 CR", "917LH": "917 LH", "917LHFE": "917 LH FE",
    "TaycanTurboS": "Taycan Turbo S", "Panamera": "Panamera", "Macan": "Macan",
    "CarreraGT": "Carrera GT", "CarreraRS": "Carrera RS", "CaymanGTS": "Cayman GTS",
    "CaymanGT4RS": "Cayman GT4 RS", "CaymanWTA": "Cayman WTA", "Boxster": "Boxster",
    "LFAFE": "LFA FE", "LC500": "LC 500", "S2000TG": "S2000 TG", "S2000WTA": "S2000 WTA",
    "CivicWTA": "Civic WTA", "CRXWTA": "CRX WTA", "CRXMugen": "CRX Mugen", "CRXsi": "CRX Si",
    "IntegraWTA": "Integra WTA", "SilviaWTA": "Silvia WTA", "SkylineWTA": "Skyline WTA",
    "SupraWTA": "Supra WTA", "ExigeWTA": "Exige WTA", "SprinterTG": "Sprinter TG",
    "SprinterFE": "Sprinter FE", "VivioRXRFE": "Vivio RX-R FE", "VivioRXR": "Vivio RX-R",
    "HongguangSFE": "Hongguang S FE", "BRZFE": "BRZ FE", "GTRFE": "GT-R FE",
    "GTRLM": "GT-R LM", "GTRNISMO": "GT-R NISMO", "GTRNismo": "GT-R Nismo",
    "GTRNISMOID": "GT-R NISMO ID", "GTRTG": "GT-R TG", "GTR": "GT-R",
    "HSVGTS": "HSV GTS", "HSVGTSMaloo": "HSV GTS Maloo", "JCWGP": "JCW GP",
    "JCWBuggy": "JCW Buggy", "MSportFiesta": "Fiesta M-Sport", "Velociraptor6x6": "Velociraptor 6x6",
    "Ultra4Bronco": "Ultra4 Bronco", "Cuda426": "Cuda 426", "ElCaminoSS": "El Camino SS",
    "BelAir": "Bel Air", "CrownVictoria": "Crown Victoria", "JPNTaxi": "JPN Taxi",
    "CrownTaxi": "Crown Taxi", "TransAmGTA": "Trans Am GTA", "FirebirdTransAm": "Firebird Trans Am",
    "GolfR": "Golf R", "GolfRallye": "Golf Rallye", "GolfGTS": "Golf GTS",
    "Type2": "Type 2", "Sports800": "Sports 800", "Sports800Fanta": "Sports 800 Fanta",
    "StarletGlanza": "Starlet Glanza", "MR2GT": "MR2 GT", "MR2SC": "MR2 SC",
    "CelicaSSI": "Celica SS-I", "CelicaGT": "Celica GT", "CelicaST185": "Celica ST185",
    "CelicaST205": "Celica ST205", "LandCruiser": "Land Cruiser",
    "LandCruiserArctic": "Land Cruiser Arctic", "JPNTaxi.": "JPN Taxi",
    "VelosterN": "Veloster N", "NVision74": "N Vision 74", "Ioniq5N": "Ioniq 5 N",
    "Mazdaspeed3": "Mazdaspeed 3", "MX5Miata": "MX-5 Miata", "MXRoadster": "MX-5 Roadster",
    "ZondaR": "Zonda R", "ZondaCinque": "Zonda Cinque", "HuayraR": "Huayra R",
    "AgeraRS": "Agera RS", "One1": "One:1", "One": "One:1", "Jesko": "Jesko",
    "ChallengerSRTHellcat": "Challenger SRT Hellcat", "ChallengerHellcat": "Challenger Hellcat",
    "ChallengerHemi": "Challenger Hemi", "ChallengerSRT": "Challenger SRT",
    "ChargerSRTHellcat": "Charger SRT Hellcat", "ChargerDaytona": "Charger Daytona",
    "ChargerRT": "Charger R/T", "Camaro1LE": "Camaro 1LE", "Rail": "Rail",
    "DurangoHellcat": "Durango Hellcat", "RamSRT10": "Ram SRT10", "Trackhawk": "Trackhawk",
    "Trailcat": "Trailcat", "Gladiator": "Gladiator", "HummerEV": "Hummer EV",
    "DebertiDriftTruck": "DeBerti Drift Truck", "DebertiMustang": "DeBerti Mustang",
    "DebertiWrangler": "DeBerti Wrangler", "HammerHeadBuggy": "Hammerhead Buggy",
    "TrophyTruck": "Trophy Truck", "Pro2Truck": "Pro 2 Truck", "Pro4": "Pro 4",
    "BajaTruck": "Baja Truck", "TacomaTRDProFE": "Tacoma TRD Pro FE",
    "TacomaTRDPro": "Tacoma TRD Pro", "4RunnerTRDPro": "4Runner TRD Pro",
    "CamryTRD": "Camry TRD", "MustangDarkHorse": "Mustang Dark Horse",
    "EscortMk2": "Escort Mk2", "EscortRSCosworth": "Escort RS Cosworth",
    "MustangGT390": "Mustang GT390", "MustangGT390FE": "Mustang GT390 FE",
    "MustangBoss302": "Mustang Boss 302", "MustangCobraR": "Mustang Cobra R",
    "MustangSVTCobraR": "Mustang SVT Cobra R", "ShelbyGT350R": "Shelby GT350R",
    "Supervan3": "Supervan 3", "ElectricSupervan": "Electric Supervan",
    "FocusRS": "Focus RS", "FocusST": "Focus ST", "FocusWRC": "Focus WRC",
    "FiestaST": "Fiesta ST", "FiestaGRC": "Fiesta GRC", "RacingPuma": "Racing Puma",
    "RS200Evolution": "RS200 Evolution", "SVT": "SVT", "Coupe": "Coupe",
    "Thunderbird": "Thunderbird", "RangerT6": "Ranger T6", "BroncoR": "Bronco R",
    "BroncoRaptor": "Bronco Raptor", "CKPickup": "CK Pickup", "TransitSSV": "Transit SSV",
    "PursuitUte": "Pursuit Ute", "Falcon": "Falcon", "FalconGT": "Falcon GT",
    "SuperBee": "Super Bee", "NovaSS": "Nova SS", "ImpalaSS": "Impala SS",
    "MonteCarlo": "Monte Carlo", "Chevelle": "Chevelle", "CorvetteStingray": "Corvette Stingray",
    "CorvetteZR1": "Corvette ZR1", "CorvetteZ06": "Corvette Z06", "Corvette": "Corvette",
    "CamaroSS": "Camaro SS", "CamaroZ28": "Camaro Z28", "CamaroZL1": "Camaro ZL1",
    "RegalGNX": "Regal GNX", "Syclone": "Syclone", "Typhoon": "Typhoon", "Jimmy": "Jimmy",
    "MonaroGTS": "Monaro GTS", "Torana": "Torana", "Sandman": "Sandman",
    "Ranchero": "Ranchero", "Fury": "Fury", "Barracuda": "Barracuda", "Dart": "Dart",
    "Demon170": "Demon 170", "Demon": "Demon", "ViperACR": "Viper ACR",
    "ViperSRT10ACR": "Viper SRT10 ACR", "ViperGTSACR": "Viper GTS ACR",
    "ViperGTSACRFE": "Viper GTS ACR FE", "Viper": "Viper", "ViperGTS": "Viper GTS",
    "EclipseGSX": "Eclipse GSX", "GalantVR4": "Galant VR-4", "Starion": "Starion",
    "PajeroEvo": "Pajero Evo", "Pajero": "Pajero", "Minicab": "Minicab",
    "LancerEvo": "Lancer Evolution", "Temerario": "Temerario", "Revuelto": "Revuelto",
    "SestoElemento": "Sesto Elemento", "Essenza": "Essenza SCV12", "SianRoadster": "Sian Roadster",
    "Countach": "Countach", "DiabloGTR": "Diablo GTR", "DiabloSV": "Diablo SV",
    "MiuraP400": "Miura P400", "Espada400GT": "Espada 400 GT", "Urus": "Urus",
    "Veneno": "Veneno", "Aventador": "Aventador", "Murcielago": "Murciélago",
    "Cobra427": "Cobra 427", "CobraDaytona": "Cobra Daytona", "GT350R": "GT350R",
    "GT500": "GT500", "GT350": "GT350", "Boss302": "Boss 302", "Raptor": "Raptor",
    "Lightning": "Lightning", "Mach1": "Mach 1", "Maverick": "Maverick",
    "FalconXR8": "Falcon XR8", "Ute": "Ute", "HSV": "HSV",
}

# Whole MediaName replacements (model text only; year is taken from the database).
MEDIA_OVERRIDES = {
    "NUL_Car_00": "Car", "343_Warthog_15": "Warthog", "FIA_124Spider_17": "124 Spider",
    "FIA_131AbarthStradale_80": "131 Abarth Stradale", "FIA_595SS_68": "595 SS",
    "FIA_695Abarth_16": "695 Abarth", "DAR_500_": "500", "FIA_Panda_87": "Panda",
    "MCL_F1_93": "F1", "MCL_F1GT_97": "F1 GTR", "POR_962CRS_93": "962 CR",
    "SUN_Imp_66": "Imp", "REL_SupervanIII_72": "Supervan III", "JAG_XJS_90": "XJS",
    "JAG_XJSFE_90": "XJS FE", "JEE_4422_Trophy_19": "4422 Trophy",
    "HON_33_IntegraWTA_01": "Integra WTA", "HON_19_CRXWTA_90": "CRX WTA",
    "HON_21_CivicWTA_92": "Civic WTA", "HON_52_S2000WTA_04": "S2000 WTA",
    "TOY_00_SupraWTA_95": "Supra WTA", "NIS_32_SkylineWTA_93": "Skyline WTA",
    "NIS_36_SilviaWTA_00": "Silvia WTA", "POR_00_CaymanWTA_05": "Cayman WTA",
    "LOT_00_ExigeWTA_18": "Exige WTA", "TOY_SprinterTG_85": "Sprinter TG",
    "HON_S2000TG_03": "S2000 TG", "NIS_00_240RSWRC_83": "240 RS",
    "TOY_GT86_Stories_13": "GT86", "HON_Acty_Jobs_94": "Acty", "FER_J50PO_17": "J50",
    "LAM_63_Essenza_21": "Essenza SCV12", "FOR_25_Ultra4Bronco_17": "Ultra4 Bronco",
    "REN_SportSpider_98": "Sport Spider", "BMW_M2Comp_20": "M2 Competition",
    "TOY_SupraGR_20": "GR Supra", "LAM_LP7004_12": "Aventador LP 700-4",
    "POR_992_19": "911 (992)", "ZEN_TSRS_19": "TSR-S", "HEN_Velociraptor6x6_18": "Velociraptor 6x6",
    "MER_XClass_18": "X-Class", "AUD_Etron_18": "e-tron", "BMW_i8_15": "i8",
    "MYT": "", "MCL_Sabre_21": "Sabre", "MAZ_00_MX5Cup_17": "MX-5 Cup",
    "FIA_124Spider_17": "124 Spider", "FOR_MSportFiesta_17": "Fiesta M-Sport",
    "JAG_CX75_10": "C-X75", "TOY_4RunnerTRDPro_19": "4Runner TRD Pro",
    "HYU_NVision74_22": "N Vision 74", "SUB_WRXSTiS209_19": "WRX STI S209",
    "POR_911GT2ID_95": "911 GT2 ID", "NIS_GTRNISMOID_24": "GT-R NISMO ID",
    "POR_70_935_19": "935", "POR_203_961_87": "961", "POR_186_959_86": "959",
    "POR_3_917LH_70": "917 LH", "POR_3_917LHFE_70": "917 LH FE",
    "FER_24_330P4_67": "330 P4", "FOR_2_GT40_66": "GT40", "MAZ_55_787B_91": "787B",
    "MAZ_123_808Wagon_74": "808 Wagon", "MIT_269_Minicab_90": "Minicab",
    "NIS_11_Skyline_84": "Skyline", "NIS_23_Skyline_98": "Skyline",
    "NIS_12_SkylineGTR_90": "Skyline GT-R", "ALF_00_SE048SP_90": "SE048 SP",
    "ALF_00_Tipo332_68": "Tipo 332", "SIE_23_Alpha_20": "23 Alpha",
    "SIE_00_RX3_21": "00 RX-3", "SIE_00_700R_21": "700R",
    "POL_51_RZRDakar_21": "RZR Dakar", "AC_122_Class1_20": "Class 1",
    "AC_6165_Class6100_21": "Class 6-100", "FOR_11_F150TrophyTruck_14": "F-150 Trophy Truck",
    "TOY_1_BajaTruck_93": "Baja Truck", "FOR_14_FiestaGRC_17": "Fiesta GRC",
    "FOR_4_FocusWRC_01": "Focus WRC", "FOR_4_ElectricSupervan_22": "Electric Supervan",
    "SUB_00_VT18X_18": "VT18X", "POR_00_MacanRR_18": "Macan RR",
    "REN_8Gordini_67": "8 Gordini", "POR_70_935_19": "935",
    "NIS_64_ZFD_23": "Z", "MIT_1_EvoTA_05": "Lancer Evo TA",
    "HYU_i30N_20": "i30 N", "HYU_i20N_22": "i20 N", "HON_e_22": "e",
    "HON_Acty_94": "Acty", "NIS_Pao_89": "Pao", "NIS_Safari_85": "Safari",
    "TOY_Altezza_99": "Altezza", "TOY_Soarer_97": "Soarer", "TOY_Chaser_97": "Chaser",
    "TOY_ChaserGT_91": "Chaser GT", "NIS_Stagea_97": "Stagea", "HON_ZGT_72": "ZGT",
    "MAZ_99_RX8FD_19": "RX-8", "CHE_777_CorvetteFD_13": "Corvette",
    "FER_117_599FD_18": "599", "BMW_98_325iFD_89": "325i", "FOR_13_MustangFD_15": "Mustang",
    "HOL_530_MalooFD_16": "Maloo", "NIS_777_240SXFD_97": "240SX", "DOD_43_ViperFD_06": "Viper",
    "TOY_34_SupraMkIV_95": "Supra Mk IV", "TOY_151_SupraGRFD_20": "Supra GR",
    "TOY_411_CorollaFD_19": "Corolla", "BMW_91_M2FD_20": "M2",
    "FOR_Supervan3_94": "Supervan 3", "MCL_600LT_19": "600LT", "MCL_765LT_21": "765LT",
    "MCL_620R_21": "620R", "MAS_MC12Corsa_06": "MC12 Corsa", "LAM_SianRoadster_21": "Sian Roadster",
    "FIA_124Spider_17": "124 Spider", "PEE_P50Trolli_62": "P50 Trolli",
    "FIA_500_": "500", "TOY_Scallop_26": "Scallop", "TOY_ScallopID_26": "Scallop ID",
    "HON_S800_70": "S800", "HON_S660_16": "S660", "TOY_Sports800_65": "Sports 800",
    "TOY_Sports800Fanta_65": "Sports 800 Fanta", "MAZ_MX5RF_22": "MX-5 RF",
    "MAZ_MX5_13": "MX-5", "MAZ_MX5_16": "MX-5", "MAZ_MX5_05": "MX-5",
    "MAZ_MiataFE_94": "Miata FE", "MAZ_00_MX5Cup_17": "MX-5 Cup",
}

# Whole words that need a fixed spelling after generic splitting.
WORD_FIXES = {
    "GTR": "GT-R", "RX7": "RX-7", "RX8": "RX-8", "RX3": "RX-3", "MX5": "MX-5",
    "GTIR": "GTI-R", "GTSR": "GTS-R", "RCF": "RC F", "CX75": "C-X75", "TSRS": "TSR-S",
    "STi": "STI", "GTi": "GTi", "Ti": "Ti", "s": "", "X": "X",
}

KEEP_GLUED = {"2000GT", "2CV", "4C", "8C", "GT40", "FJ40", "GT86", "GT500", "GT350", "BRZ"}


def humanize_token(token: str) -> str:
    override = TOKEN_OVERRIDES.get(token)
    if override is not None:
        return override
    if token in KEEP_GLUED:
        return token
    text = re.sub(r'(?<=[a-z])(?=[A-Z])', ' ', token)            # fooBar -> foo Bar
    text = re.sub(r'(?<=[a-z])(?=[0-9])', ' ', text)             # Cobra427 -> Cobra 427
    text = re.sub(r'(?<=[0-9])(?=[A-Z][a-z])', ' ', text)        # 355Berlinetta -> 355 Berlinetta
    text = re.sub(r'(?<=[0-9])(?=[A-Z]{2})', ' ', text)          # 911GT2 -> 911 GT2
    words = []
    for word in text.split():
        fixed = WORD_FIXES.get(word)
        if fixed is not None:
            if fixed:
                words.append(fixed)
            continue
        if word.islower() and len(word) <= 2 and words:
            # lowercase fragment left over from an acronym split ("CRX si" -> "CRX Si")
            words[-1] = words[-1] + word.capitalize()
        else:
            words.append(word)
    return ' '.join(' '.join(words).split())


def model_of(media: str, make_id: int) -> str:
    override = MEDIA_OVERRIDES.get(media)
    if override is not None:
        return override
    parts = media.split('_')
    body = [p for p in parts[1:] if p]
    while body and re.fullmatch(r'\d{2}', body[-1]):
        body = body[:-1]
    # Leading pure-digit tokens are livery/special-edition markers (e.g. #13, 777); keep the
    # number only when it is the whole model name (Datsun 2000).
    while len(body) > 1 and re.fullmatch(r'\d+', body[0]):
        body = body[1:]
    if make_id == 172:  # Formula Drift: the "FD" tag and #number are internal markers.
        body = [re.sub(r'FD$', '', t) if len(t) > 2 else t for t in body]
    words = [humanize_token(t) for t in body]
    model = ' '.join(w for w in words if w).strip()
    if make_id == 137:  # Mercedes-AMG: the make already carries "AMG".
        model = re.sub(r'\bAMG\s+', '', model).strip()
    if make_id == 206:  # DeBerti
        model = re.sub(r'\bDeBerti\b\s*', '', model).strip()
    if make_id == 172:
        model = re.sub(r'\bFormula Drift\b', '', model)
        model = re.sub(r'\b(Stories|Jobs|Traffic)\b', '', model)
    model = re.sub(r'\bTraffic\b', '', model)
    return ' '.join(model.split())


def load_rows(db_dir, table):
    path = os.path.join(db_dir, table + '.tsv')
    with open(path, encoding='utf-8', newline='') as f:
        rows = list(csv.reader(f, delimiter='\t'))
    header = rows[0]
    if header[0] == header[1]:
        header = ['_rowid'] + header[1:]
    return [dict(zip(header, row)) for row in rows[1:]]


def num_val(value, scale=1.0, digits=None):
    if value in (None, '', 'NULL'):
        return 0.0
    result = float(value) * scale
    return round(result, digits) if digits is not None else result


def g(value):
    """Shortest round-trip-style number formatting for the pipe-separated data file."""
    if isinstance(value, str):
        return value
    if value == int(value):
        return str(int(value))
    return f'{value:.3f}'.rstrip('0').rstrip('.')


def year_of(car):
    year = int(car['Year']) if car['Year'] else 0
    if 1886 <= year <= 2035:
        return year
    media_year = re.search(r'_(\d{2})$', car['MediaName'])
    if media_year:
        yy = int(media_year.group(1))
        return 2000 + yy if yy <= 35 else 1900 + yy
    return 0


def main():
    parser = argparse.ArgumentParser(description="Build the offline vehicle catalog from an existing TSV.")
    parser.add_argument("tsv_dir", help="Directory containing Data_Car_c0.tsv")
    parser.add_argument("output", nargs="?", default=DEFAULT_OUT)
    args = parser.parse_args()
    out_path = os.path.abspath(args.output)
    cars = sorted(load_rows(args.tsv_dir, 'Data_Car_c0'), key=lambda c: int(c['Id']))
    if not cars:
        parser.error("Data_Car_c0.tsv contains no vehicle records")

    car_lines, review, used_media, used_tokens = [], [], set(), set()
    for car in cars:
        car_id = int(car['Id'])
        make_id = int(car['MakeID'])
        media = car['MediaName']
        model = model_of(media, make_id)
        if media in MEDIA_OVERRIDES:
            used_media.add(media)
        for token in media.split('_')[1:]:
            if token in TOKEN_OVERRIDES:
                used_tokens.add(token)
        make_zh, make_en = MAKES.get(make_id, ('', ''))
        if not make_zh and not make_en:
            raise SystemExit(f'unknown MakeID {make_id} for car {car_id} ({media})')
        country_id = int(car['CountryID']) if car['CountryID'] else 0
        country_zh, country_en = COUNTRIES.get(country_id, ('', ''))
        cylinders, rotors = CYLINDERS.get(int(car['CylinderID']), (0, 0))
        fields = [
            str(car_id), str(year_of(car)), str(make_id), model, media,
            str(country_id), str(int(car['DriveTypeID']) if car['DriveTypeID'] else 0),
            str(cylinders), str(rotors),
            str(int(car['AspirationTypeId']) if car['AspirationTypeId'] else 0),
            str(int(car['EngineConfigID']) if car['EngineConfigID'] else 0),
            g(num_val(car['Displacement'], 1, 0)),
            g(num_val(car['CurbWeight'], 100, 1)),
            g(num_val(car['WeightDistribution'], 1, 3)),
            str(int(car['NumGears']) if car['NumGears'] else 0),
            g(num_val(car['SimPeakPower'], 0.1, 1)),
            g(num_val(car['SimPeakTorque'], 100, 1)),
            g(num_val(car['SimTopSpeed'], 3.6, 1)),
            g(num_val(car['SimRedlineAngVel'], 60 / (2 * 3.141592653589793), 0)),
            g(num_val(car['FrontTireWidthMM'], 1, 0)),
            g(num_val(car['FrontTireAspect'], 1, 0)),
            g(num_val(car['FrontWheelDiameterIN'], 1, 0)),
            g(num_val(car['RearTireWidthMM'], 1, 0)),
            g(num_val(car['RearTireAspect'], 1, 0)),
            g(num_val(car['RearWheelDiameterIN'], 1, 0)),
        ]
        car_lines.append('|'.join(fields))
        review.append(f"{car_id:>5} {media:<34} {fields[1]} {make_zh} {model}")

    def block(lines):
        return '\n'.join(lines)

    make_lines = [f"{k}|{MAKES[k][0]}|{MAKES[k][1]}" for k in sorted(MAKES)]
    country_lines = [f"{k}|{COUNTRIES[k][0]}|{COUNTRIES[k][1]}" for k in sorted(COUNTRIES)]
    aspiration_lines = [f"{k}|{v}" for k, v in sorted(ASPIRATIONS.items())]
    engine_lines = [f"{k}|{v}" for k, v in sorted(ENGINE_CONFIGS.items())]
    cylinder_lines = [f"{k}|{v[0]}|{v[1]}" for k, v in sorted(CYLINDERS.items())]
    drive_lines = [f"{k}|{DRIVES[k][0]}|{DRIVES[k][1]}" for k in sorted(DRIVES)]

    source = f'''// <auto-generated>
// 由 tools/generate_car_catalog.py 从已有的离线 TSV 整理。此车型资料随应用编译。
// 每条车型的原始标识见 MediaName 列。车厂、产地及部分型号的显示名由脚本内的映射表补全。
// </auto-generated>
namespace FH6Telemetry;

/// <summary>生成的车辆档案原始数据（管道分隔），解析逻辑见 <see cref="VehicleCatalog"/>。</summary>
internal static class VehicleCatalogData
{{
    public const int SchemaVersion = 1;

    public const string Makes = """
{block(make_lines)}
""";

    public const string Countries = """
{block(country_lines)}
""";

    public const string Aspirations = """
{block(aspiration_lines)}
""";

    public const string EngineConfigs = """
{block(engine_lines)}
""";

    public const string Cylinders = """
{block(cylinder_lines)}
""";

    public const string Drives = """
{block(drive_lines)}
""";

    public const string Cars = """
{block(car_lines)}
""";
}}
'''
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    with open(out_path, 'w', encoding='utf-8', newline='\n') as f:
        f.write(source)
    os.makedirs(os.path.dirname(REVIEW_OUT), exist_ok=True)
    with open(REVIEW_OUT, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(review))
    print(f'wrote {out_path} ({len(car_lines)} cars, {len(MAKES)} makes)')
    unused = (set(TOKEN_OVERRIDES) - used_tokens) | (set(MEDIA_OVERRIDES) - used_media)
    if unused:
        print(f'note: {len(unused)} unused override keys (harmless): {sorted(unused)[:20]}')
    print(f'review: {REVIEW_OUT}')


if __name__ == '__main__':
    main()
