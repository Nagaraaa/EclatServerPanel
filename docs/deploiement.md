# Installer le panel sur un VPS

L’installateur configure l’administration et les services Linux. Il n’installe pas la distribution privée du jeu, Node.js ou le tunnel Internet propre au serveur.

Les exemples utilisent `vps.example.invalid`. Le remplacer par l’adresse de son VPS. Les commandes d’installation se font avec un accès administrateur ; le panel utilisera ensuite le compte limité `eclat-panel`.

## Préparer l’accès du PC

Créer une clé dédiée dans PowerShell :

```powershell
ssh-keygen -t ed25519 -f "$env:USERPROFILE\.ssh\eclat_panel" -C "eclat-panel"
```

Conserver le fichier sans extension sur le PC. Seul le fichier `.pub` doit être transféré au VPS. Si une phrase secrète est choisie, charger la clé dans l’agent OpenSSH avant de lancer le panel :

```powershell
ssh-add "$env:USERPROFILE\.ssh\eclat_panel"
```

Vérifier préalablement l’empreinte du VPS dans la console de l’hébergeur, comme décrit dans les [notes de sécurité](securite.md).

Depuis la racine du dépôt, transférer les fichiers génériques et la clé publique :

```powershell
scp -r server root@vps.example.invalid:/root/eclat-panel-setup
scp "$env:USERPROFILE\.ssh\eclat_panel.pub" root@vps.example.invalid:/root/eclat-panel.pub
```

## Installer les services

Sur le VPS :

```sh
bash /root/eclat-panel-setup/install.sh /root/eclat-panel.pub
```

L’installateur :

- installe Python 3, SQLite, OpenSSH et `sudo` depuis les paquets Debian ;
- crée les comptes séparés `eclat-server` et `eclat-panel` ;
- installe l’outil `/usr/local/sbin/eclat-admin` ;
- associe la clé du panel à une commande SSH forcée et à une règle `sudo` limitée à cet outil sans arguments ;
- installe le service du jeu, un contrôle de disponibilité et une sauvegarde quotidienne ;
- active les unités pour les prochains démarrages et lance les minuteurs.

Le service du jeu n’est ni démarré ni arrêté pendant l’installation. Les minuteurs seront actifs : préparer les chemins et les données avant de mettre le jeu en service. Relancer l’installateur remplace la clé publique autorisée du panel ; il conserve un fichier `/etc/eclat-panel.json` déjà présent.

## Fournir le serveur de jeu

La configuration fournie attend ces chemins :

| Élément | Chemin |
| --- | --- |
| Distribution privée | `/opt/eclat-originel/app` |
| Node.js | `/usr/local/bin/node` |
| Données persistantes | `/var/lib/eclat-originel` |
| Instantanés privés | `/var/backups/eclat-originel` |
| Configuration de l’administration | `/etc/eclat-panel.json` |

Installer une version de Node.js compatible avec la distribution du jeu, au minimum **24.14** pour cette installation. Fournir les dépendances et le binaire du tunnel requis par le lanceur privé. Vérifier leurs versions et leur fonctionnement sous Linux.

L’unité fournie lance `internet-launch.mjs start --data /var/lib/eclat-originel`. Si la distribution utilise une autre commande, adapter l’unité `eclat-server.service` avant de la démarrer. La distribution doit être lisible par `eclat-server` ; ses fichiers persistants doivent être accessibles à ce compte dans le dossier de données. Les fichiers du programme et de l’outil d’administration doivent rester non modifiables par le compte du panel.

Vérifier `/etc/eclat-panel.json` : nom du service, dossier de données, nom du fichier SQLite, port local et fichiers privés à inclure dans les instantanés. Le fichier reste réservé à root. Ne pas y placer une commande arbitraire envoyée par le client.

Le protocole attendu est du JSON délimité par des retours à la ligne sur TCP. Le contrôle ouvre une connexion locale, envoie `hello`, attend `hello.ok`, puis envoie `ping` et attend `ping.ok`. Le port configuré par défaut est `8787`.

## Démarrer et vérifier

Quand la distribution et les données sont prêtes :

```sh
systemctl daemon-reload
systemctl start eclat-server.service
systemctl status eclat-server.service
```

Contrôler aussi l’outil d’administration :

```sh
printf '{"action":"status"}\n' | /usr/local/sbin/eclat-admin
printf '{"action":"backup"}\n' | /usr/local/sbin/eclat-admin
```

Sur le PC, ouvrir le panel et renseigner :

- Hôte : l’adresse du VPS.
- Port : le port SSH configuré sur le VPS, généralement `22`.
- Utilisateur : `eclat-panel`.
- Clé : le chemin de `eclat_panel`, sans l’extension `.pub`.

Valider l’empreinte après comparaison avec la console, puis vérifier l’état et télécharger une sauvegarde. Faire ensuite une connexion depuis le client de jeu : le test local du panel ne vérifie pas toute la route Internet des joueurs.

## Mettre à jour le panel et l’outil Linux

Décompresser la nouvelle version Windows dans un dossier distinct, puis ouvrir son exécutable. Les réglages et les clés restent dans le profil Windows. Garder l’ancien dossier permet de revenir à la version précédente.

Pour mettre à jour uniquement l’outil Linux, copier `server/eclat-admin.py` vers `/root/eclat-admin.new.py` sur le VPS, puis exécuter depuis le compte administrateur :

```sh
cp -p /usr/local/sbin/eclat-admin /root/eclat-admin.previous.py
python3 -m py_compile /root/eclat-admin.new.py
install -o root -g root -m 0755 /root/eclat-admin.new.py /usr/local/sbin/eclat-admin
printf '{"action":"status"}\n' | /usr/local/sbin/eclat-admin
```

Cette mise à jour ne remplace ni la configuration ni les données du jeu et ne redémarre pas son service. Si le contrôle échoue, rétablir l’ancien outil avec `install -o root -g root -m 0755 /root/eclat-admin.previous.py /usr/local/sbin/eclat-admin`, puis relancer le contrôle. Ne pas restaurer une ancienne base pour revenir à une ancienne version du panel.

Le panel accepte également les réponses des versions précédentes de l’outil. La version actuelle vérifie en plus que le processus qui fournit le suivi des joueurs appartient au service du jeu ; cela évite de montrer l’activité d’un processus terminé.

## Contrôle de disponibilité et sauvegardes

Le minuteur `eclat-watchdog.timer` contrôle le jeu environ chaque minute. Après trois échecs consécutifs du protocole, il redémarre le service. Il laisse un service volontairement arrêté dans cet état. Un redémarrage peut interrompre une partie ; consulter les journaux si cela se répète.

Le minuteur `eclat-backup.timer` prévoit un instantané quotidien à **04 h UTC**, avec un décalage aléatoire de dix minutes au maximum. `Persistent=true` permet de rattraper une exécution manquée lorsque le minuteur revient en service.

Les instantanés locaux contiennent une copie SQLite cohérente, un manifeste et les fichiers privés configurés qui existent. Ils sont réservés à root. Le panel peut en exporter la base seule et restaurer cette base, avec une limite de 64 Mio. Surveiller l’espace disponible et définir une durée de conservation adaptée à l’activité du serveur.

Pour consulter les prochaines exécutions :

```sh
systemctl list-timers eclat-watchdog.timer eclat-backup.timer
```

Les sauvegardes du fournisseur, les instantanés locaux et les copies téléchargées sur le PC couvrent des besoins différents. Conserver au moins une copie récente hors du VPS.
