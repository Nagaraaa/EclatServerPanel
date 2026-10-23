# Accès et données privées

## Modèle d’accès

Le PC conserve une clé SSH dédiée. Sa clé publique est installée pour un compte d’administration du jeu sur le VPS. Ce compte lance uniquement l’outil fourni avec le panel, via une commande SSH forcée ; il n’a pas de terminal ni de transfert de ports.

L’outil privilégié accepte des actions définies à l’avance. Les noms de service, dossiers et chemins utiles sont définis sur le VPS. Le panel ne doit pas pouvoir envoyer une commande système arbitraire, un nom de service libre ou un chemin choisi à distance.

Les opérations de service et de restauration ont besoin de privilèges sur le VPS. Une règle `sudo` doit viser uniquement l’outil d’administration, jamais un shell, un interpréteur généraliste ou `systemctl` sans restriction. L’installation initiale se fait avec un accès administrateur distinct.

## Vérifier la clé d’hôte

Avant d’accepter l’empreinte dans l’application, relever celle du VPS dans la console de l’hébergeur :

```sh
ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
```

Comparer la valeur SHA-256 complète. Une collecte par `ssh-keyscan` montre la clé présentée sur le réseau ; elle ne l’authentifie pas. Le panel utilise ensuite `StrictHostKeyChecking=yes`.

Si la clé change après une réinstallation, vérifier la nouvelle empreinte dans la console avant de remplacer celle enregistrée. Ne pas supprimer l’ancienne empreinte pour contourner un avertissement inexpliqué.

## Ce qui reste privé

- Clé SSH privée et éventuelle phrase secrète.
- Réglages de connexion, adresse du VPS et empreintes enregistrées.
- Configuration du jeu, secrets d’annuaire ou de tunnel.
- Bases SQLite, instantanés, identifiants des joueurs et journaux.

Le fichier `.gitignore` réduit le risque d’ajout accidentel. Il ne protège pas un fichier déjà suivi par Git et ne remplace pas une vérification du contenu avant publication. Les exemples du dépôt doivent employer des valeurs fictives.

Le panel n’embarque pas de mot de passe root. Un compte root sert à préparer le système ; il n’est pas utilisé pour les opérations quotidiennes du panel.

## Journaux et sauvegardes

L’outil masque les motifs sensibles qu’il reconnaît dans les journaux. Un message inattendu, un nom de joueur ou un secret dans un format différent peut rester visible. Relire toute extraction avant de la publier.

Une sauvegarde téléchargée contient des données privées du jeu. La conserver dans un dossier protégé et éviter de la synchroniser vers un dépôt public. La vérification SHA-256 détecte une copie altérée ; elle ne chiffre pas le fichier.

Les instantanés conservés sur le VPS peuvent inclure des fichiers de configuration privés. Le panel télécharge uniquement la base SQLite extraite de l’instantané. La restauration porte également sur la base ; la configuration courante du serveur reste celle de l’installation.

Une restauration est destructive pour les données actuelles. Elle doit arrêter le service, vérifier la base source et conserver une copie de retour arrière avant de remplacer la base. Le panel demande une confirmation explicite. Après restauration, vérifier les données et une connexion de jeu avant de rouvrir le serveur aux joueurs.

## Limites

Le compte dédié et les commandes fixes réduisent les possibilités d’action depuis le panel. Une clé volée peut tout de même permettre d’arrêter le jeu ou d’accéder aux sauvegardes autorisées. Protéger la session Windows, restreindre l’accès au fichier de clé et révoquer sa clé publique sur le VPS si le PC est compromis.

Le contrôle de disponibilité vérifie le protocole attendu. Il ne garantit pas qu’une partie entière fonctionne, que le tunnel public reste accessible depuis tous les réseaux ou que le VPS ne subira pas de panne.
