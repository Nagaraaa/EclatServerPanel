# Éclat Server Panel

Application Windows pour administrer un serveur de jeu Éclat à distance : voir s’il répond, consulter les journaux, le relancer et conserver des sauvegardes de sa base SQLite.

Le panel passe par OpenSSH et une clé privée conservée sur votre PC. Le serveur continue de tourner lorsque l’application est fermée. Aucun port d’administration Web supplémentaire n’est nécessaire.

![Vue d’ensemble du panel en mode démonstration](docs/panel.png)

*Aperçu avec des données fictives.*

L’interface sombre suit l’état du serveur en direct : orbe d’état qui pulse quand le jeu répond, compteurs et jauges animés, courbes des joueurs, salons, latence et charge sur les dernières actualisations. Les actions sensibles (arrêt, redémarrage, restauration) se confirment dans la fenêtre même ; les résultats s’affichent en notifications.

| Joueurs et salons | Journaux | Sauvegardes |
| --- | --- | --- |
| ![Salons et joueurs](docs/captures/sessions.png) | ![Journaux filtrables](docs/captures/logs.png) | ![Instantanés et restauration](docs/captures/backups.png) |
| **Diagnostic** | **Connexion** | **Confirmation** |
| ![Diagnostic structuré](docs/captures/diagnostics.png) | ![Connexion en trois étapes](docs/captures/connection.png) | ![Restauration confirmée par saisie](docs/captures/restore-dialog.png) |

## Fonctions

| Action | Fonctionnement |
| --- | --- |
| Vérifier l’état | État du service et échange `hello` / `ping` avec le jeu. Un processus actif ne suffit pas à déclarer le jeu disponible. |
| Voir l’activité | Joueurs et salons remontés par le serveur. |
| Contrôler le service | Démarrer, arrêter ou redémarrer le service du jeu. |
| Lire les journaux | Dernières lignes du service, avec masquage des valeurs sensibles reconnues. |
| Suivre les ressources | Mémoire, charge et espace disque du VPS. |
| Sauvegarder | Créer un instantané SQLite cohérent et en télécharger une copie vérifiée par SHA-256. |
| Restaurer | Choisir une sauvegarde, confirmer la restauration et conserver une copie de la base précédente. |

Le dépôt contient le panel et son outil d’administration Linux. Il ne contient ni ROM, ni sprites, ni code du serveur de jeu, ni données de joueurs. La distribution du serveur se fournit séparément.

## Prérequis

**Sur le PC :** Windows 10 ou 11 en 64 bits, avec le client OpenSSH Windows (`ssh`, `ssh-keyscan`, `ssh-keygen`). Une compilation autonome inclut le runtime .NET.

**Sur le VPS :** Debian avec systemd, Python 3, SQLite et Node.js **24.14 ou plus récent** pour la distribution du jeu prévue ici. L’installation initiale demande un accès administrateur. L’application utilise ensuite un compte SSH dédié.

Le panel est conçu pour un serveur Éclat compatible avec l’outil Linux de ce dépôt. Il ne détecte pas automatiquement les commandes de n’importe quel serveur Pokémon.

## Télécharger

Prendre l'archive Windows dans les [versions publiées](https://github.com/Nagaraaa/EclatServerPanel/releases/latest), la décompresser puis ouvrir `EclatServerPanel.exe`. Le runtime .NET est inclus. Le fichier `SHA256SUMS.txt` de la version permet de vérifier l'archive.

## Première connexion

1. Préparer le VPS en suivant le [guide de déploiement](docs/deploiement.md).
2. Ouvrir le panel et renseigner l’hôte, le port SSH, le compte dédié et le chemin de la clé privée.
3. Afficher l’empreinte du serveur. La comparer à l’empreinte obtenue dans la console du fournisseur avant de l’accepter.
4. Tester la connexion et consulter l’état du jeu.

Les réglages sont enregistrés dans `%LOCALAPPDATA%\EclatServerPanel\connection.json`. Ils contiennent l’adresse du serveur et le chemin de la clé, jamais son contenu. Les clés d’hôte acceptées sont conservées dans un fichier `known_hosts` du même dossier. Aucun mot de passe root n’est demandé ou enregistré par le panel.

Les connexions sont non interactives. Si la clé possède une phrase secrète, la charger préalablement dans l’agent OpenSSH de Windows avec `ssh-add` ; l’application n’affiche pas de demande de phrase secrète.

Une empreinte obtenue par `ssh-keyscan` doit être vérifiée par un autre moyen : elle ne prouve pas, à elle seule, l’identité du VPS. Les connexions suivantes exigent la même clé d’hôte.

## Compiler

Installer le SDK .NET 8, puis depuis la racine du dépôt :

```powershell
dotnet build src/EclatServerPanel/EclatServerPanel.csproj -c Release
dotnet publish src/EclatServerPanel/EclatServerPanel.csproj -c Release -r win-x64 --self-contained true -o dist/win-x64
```

Lancer `dist\win-x64\EclatServerPanel.exe`. Le dossier produit est portable ; les préférences restent dans le profil Windows.

Pour voir l’interface sans se connecter à un serveur :

```powershell
.\dist\win-x64\EclatServerPanel.exe --demo
```

Le mode démo utilise des données fictives et ne permet pas de piloter le VPS. `--capture-demo-pages DOSSIER` enregistre une capture de chaque page de la démo, après ses animations, ainsi que la fenêtre de confirmation de restauration.

## Exploitation

- Une sauvegarde SQLite couvre les données de la base. Elle ne remplace pas une copie de la configuration et de la distribution privée du jeu.
- Une image automatique de l’hébergeur facilite la reprise du VPS ; elle ne remplace pas les sauvegardes cohérentes de SQLite.
- Garder une copie des sauvegardes hors du VPS, par exemple sur le PC.
- Une restauration remplace les données actuelles par celles de la sauvegarde choisie. Les modifications postérieures ne sont pas fusionnées.
- Le téléchargement et la restauration via le panel sont limités aux bases de 64 Mio. Le téléchargement exporte uniquement la base SQLite ; les fichiers de configuration privés restent sur le VPS.
- L’accès SSH du panel est limité à des commandes prévues dans l’outil d’administration. Il ne fournit pas de terminal distant.

Le [guide de migration](docs/migration.md) décrit la bascule depuis un ancien hôte. Les [notes de sécurité](docs/securite.md) précisent les accès et les limites du masquage des journaux.

## Validation

Le dépôt prévoit une compilation Windows et des vérifications automatisées dans [GitHub Actions](.github/workflows/build.yml). Ces vérifications couvrent le code du panel et de l’outil d’administration ; elles ne prouvent pas qu’un VPS donné est disponible ou qu’une partie coop fonctionne.

Vérifications du panel sous Windows :

```powershell
dotnet run --project src/EclatServerPanel/EclatServerPanel.csproj -c Release -- --self-test
```

Vérifications de l’outil d’administration sous Linux, avec Python 3 :

```sh
python -m unittest discover -s tests -v
```

Après toute installation, vérifier séparément l’accès SSH, les réponses du jeu, une sauvegarde téléchargeable et une connexion depuis un client réel. Tester une restauration sur une base de test avant de s’en servir sur des données importantes.

## Licence

Code sous [licence MIT](LICENSE). Les noms de jeux et marques mentionnés restent la propriété de leurs détenteurs. Ce projet ne distribue aucun contenu Nintendo et n’est pas affilié à Nintendo, Game Freak ou The Pokémon Company.
