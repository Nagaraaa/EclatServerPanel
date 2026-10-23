# Actions de l’outil Linux

L’outil lit une requête JSON sur son entrée standard et renvoie une seule réponse. Son accès SSH reste limité à ce programme. Aucun champ ne permet d’ouvrir un terminal, de choisir une requête SQL ou d’exécuter un chemin envoyé par le client.

La réponse garde l’enveloppe existante : `{"ok":true,"data":{...}}` ou `{"ok":false,"error":"message"}`. Une erreur de transport après l’envoi d’une commande ne prouve pas son échec. Consulter le journal avant de recommencer ; l’outil ne renvoie jamais automatiquement une mutation.

## Actions ajoutées

| Action | Champs supplémentaires | Données renvoyées |
| --- | --- | --- |
| `admin.overview` | Aucun | Maintenance, joueurs connectés, comptes, bannissements, salons, capacités disponibles. |
| `admin.audit` | Aucun | `items`, jusqu’à 200 entrées datées du serveur et de l’outil. `serverAuditUnavailable` indique une lecture partielle si le serveur ne répond pas. |
| `admin.command` | `requestId`, `command`, `arguments` | Résultat confirmé par le serveur. |
| `config.get` | Aucun | `config`, `sha256`, `exists`, `status`, `capabilities`. |
| `config.save` | `requestId`, `config`, `expectedSha256`, `reason` | `sha256`, `accepted`, `status`, `version`. |
| `config.history` | Aucun | `items` : `id`, `modifiedUtc`, `sha256`, `reason`. |
| `config.restore` | `requestId`, `version`, `expectedSha256`, `reason` | Même résultat que l’enregistrement. |
| `statistics` | Aucun | `totals`, `drawsByBanner`, `casinoByGame`, `refundTotals`, `activePlayersByDay` et portée des chiffres. |
| `monitor` | Aucun | `service` et `monitor` seulement (joueurs, positions, salons). Pas de connexion de contrôle au jeu ni de lancement de Node : la carte s’actualise ainsi toutes les 5 secondes. |

Les actions existantes restent disponibles.

## Session

Une requête `{"action":"session"}` ouvre une session : l’outil répond `{"ok":true,"data":{"session":1,"idleSeconds":600}}`, puis traite une requête JSON par ligne et renvoie une réponse par ligne, jusqu’à la fin de l’entrée, après 10 minutes sans requête ou après 4 heures. Le panel garde ainsi une seule connexion SSH au lieu d’une poignée de main par actualisation (OpenSSH sous Windows ne mutualise pas les connexions).

Chaque requête de la session est validée et verrouillée exactement comme un appel isolé ; une erreur ne ferme pas la session. Le panel ne répète jamais une mutation après une coupure de session ; il reprend une lecture par un appel isolé. Face à un outil antérieur, qui attend la fin de l’entrée, le panel abandonne la session après 20 secondes et revient à un appel par action pendant 6 heures (ou jusqu’au redémarrage du panel).

La version de Node affichée dans l’état est mise en cache dans `/run/eclat-panel-node-version.json` et recalculée seulement quand l’exécutable change. Les empreintes SHA-256 des sauvegardes sont calculées par blocs de 1 Mio. L’action `close-room` conserve la compatibilité avec le bouton de fermeture de salon existant et utilise désormais le même canal versionné que `room.close`.

`requestId` est un UUID propre à l’opération. Le serveur conserve le résultat des mutations afin qu’une répétition de la même requête n’ajoute pas un second crédit. Les changements de configuration refusent un identifiant déjà utilisé ; recharger leur état et consulter le journal avant une nouvelle tentative. Toutes les mutations prennent le même verrou d’opération que les sauvegardes et les restaurations SQLite.

## Commandes du serveur

| Commande | Arguments |
| --- | --- |
| `player.kick` | `playerId` UUID |
| `account.ban` | `accountHash`, `reason`, `expiresAt` date UTC ISO terminée par `Z`, ou `null` |
| `account.unban` | `accountHash`, `reason` facultatif |
| `room.close` | `roomId` UUID |
| `room.update` | `roomId` UUID et au moins un de `capacity` de 2 à 10, `visibility` valant `public` ou `private` |
| `maintenance.set` | `enabled` booléen |
| `eclats.adjust` | `accountHash`, `amount` entier signé non nul, `reason` |
| `casino.refund` | `accountHash`, `amount` entier de 1 à 9999, `reason` |

Un motif contient de 3 à 240 caractères, sans espaces au début ou à la fin ni caractères de contrôle. Une empreinte de compte contient 64 caractères hexadécimaux minuscules. La clé du compte n’est jamais un argument accepté. Le panel n’affiche que le début de l’empreinte ; une nouvelle clé permet de contourner un bannissement par compte.

Le canal Unix reçoit `{"version":1,"requestId":"UUID","command":"…","arguments":{…}}`. Sa réponse est limitée à 1 Mio et son échange à 35 secondes. La socket doit appartenir au compte de service avec le mode `0600`. Les commandes suivent une liste fermée, validée à nouveau par le serveur.

## Configuration et statistiques

La configuration brute est limitée à 64 Kio. Si le fichier n’existe pas, `config.get` demande la configuration par défaut au validateur réel du jeu ; son checksum reste celui du fichier absent, soit le SHA-256 des octets vides. Le prochain enregistrement peut ainsi créer le fichier sans inventer de valeurs par défaut dans l’outil Python.

Si un autre outil a laissé un fichier JSON illisible, la lecture renvoie son vrai checksum, `fileInvalid:true`, `raw` et une `recoveryConfig` proposée par le validateur du jeu. Aucun remplacement n’est automatique. Cette proposition permet de réparer le fichier ou de restaurer une version précédente en conservant la protection contre les modifications concurrentes. Une ancienne version contenant des octets UTF-8 invalides reste conservée comme telle dans l’historique ; elle doit toujours passer le validateur avant une éventuelle restauration.

`status.state` vaut `accepted`, `rejected` ou `pending`. Une acceptation exige un suivi récent du processus du service et l’égalité entre le checksum écrit et `loadedSha256`. `accepted:false` accompagné de `pending` signifie que le fichier a pu être écrit mais que le jeu ne l’a pas encore confirmé. Une tentative refusée à chaud laisse le serveur utiliser sa dernière configuration valide.

Les statistiques n’exportent pas de clé ou de liste nominative. Les totaux couvrent les données conservées dans la base. Les chiffres par jour couvrent les trente derniers jours, avec `dayTimezone:"UTC"`. `activityScope:"authenticatedConnections"` correspond au suivi des comptes connectés, disponible depuis `activityAvailableSince`. Le repli `activityScope:"economy"` ne couvre que les opérations économiques des anciennes bases.

Les tours gratuits ne sont pas ajoutés aux mises. Les remboursements administratifs figurent dans `refundTotals`, et non parmi les parties ou les gains du casino.

## Essais locaux

Depuis la racine du dépôt, sous Linux :

```sh
python3 -m unittest discover -s tests -v
bash -n server/install.sh
```

Les tests créent leurs propres fichiers, bases et sockets temporaires. Ils ne lisent ni ne modifient les données d’un VPS.

Pour vérifier aussi le vrai validateur et le vrai canal du serveur, fournir une distribution de développement déjà compilée du jeu :

```sh
ECLAT_NODE_BINARY=/chemin/vers/node \
ECLAT_SERVER_BUILD=/chemin/vers/pokemon-eclat-originel/dist \
python3 -m unittest discover -s tests -p test_admin_integration.py -v
```

Ces essais lancent un serveur local avec une nouvelle base temporaire. Ils vérifient les huit commandes, les refus de solde négatif, le mode maintenance, les écritures de configuration, leur rechargement par checksum, une restauration de configuration et les statistiques en lecture seule. Le seul contrôle de processus simulé concerne l’appartenance à systemd : le serveur d’essai n’est pas installé en service. Le jeu, la socket, le validateur et le rechargement restent réels.
