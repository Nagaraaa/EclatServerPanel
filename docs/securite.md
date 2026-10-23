# Accès et données privées

## Modèle d’accès

Le PC conserve une clé SSH dédiée. Sa clé publique est installée pour un compte d’administration du jeu sur le VPS. Ce compte lance uniquement l’outil fourni avec le panel, via une commande SSH forcée ; il n’a pas de terminal ni de transfert de ports.

L’outil privilégié accepte des actions définies à l’avance. Les noms de service, dossiers et chemins utiles sont définis sur le VPS. Le panel ne doit pas pouvoir envoyer une commande système arbitraire, un nom de service libre ou un chemin choisi à distance.

Les opérations de service et de restauration ont besoin de privilèges sur le VPS. Une règle `sudo` doit viser uniquement l’outil d’administration, jamais un shell, un interpréteur généraliste ou `systemctl` sans restriction. L’installation initiale se fait avec un accès administrateur distinct.

Dans la préversion carte et commandes, le serveur ouvre une socket Unix locale, réservée au compte du service avec les droits `0600`. L’outil vérifie son propriétaire et l’identité du processus qui répond. Le canal reste sur le VPS et n’ajoute aucun port réseau. Il accepte une liste fermée de commandes dont les arguments sont vérifiés par l’outil puis par le serveur.

Les opérations qui modifient l’état partagent un verrou. Chaque commande reçoit un identifiant propre et le serveur conserve son résultat pour éviter un second crédit si la même requête est répétée. Le panel ne recommence pas automatiquement une commande après un délai dépassé : son résultat peut être inconnu. Lire l’état et le journal avant une nouvelle tentative.

## Vérifier la clé d’hôte

Avant d’accepter l’empreinte dans l’application, relever celle du VPS dans la console de l’hébergeur :

```sh
ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub
```

Comparer la valeur SHA-256 complète. Une collecte par `ssh-keyscan` montre la clé présentée sur le réseau ; elle ne l’authentifie pas. Le panel utilise ensuite `StrictHostKeyChecking=yes`.

La confiance est associée à l’adresse et au port SSH. L’indicateur de connexion vérifie cette entrée précise, même si le fichier contient déjà des empreintes pour d’autres serveurs. Une nouvelle validation remplace uniquement les entrées de la connexion confirmée.

Si la clé change après une réinstallation, vérifier la nouvelle empreinte dans la console avant de remplacer celle enregistrée. Ne pas supprimer l’ancienne empreinte pour contourner un avertissement inexpliqué.

## Ce qui reste privé

- Clé SSH privée et éventuelle phrase secrète.
- Réglages de connexion, adresse du VPS et empreintes enregistrées.
- Configuration du jeu, secrets d’annuaire ou de tunnel.
- Bases SQLite, instantanés, identifiants des joueurs et journaux.
- Positions des joueurs connectés, informations de progression et historique d’administration.

Le fichier `.gitignore` réduit le risque d’ajout accidentel. Il ne protège pas un fichier déjà suivi par Git et ne remplace pas une vérification du contenu avant publication. Les exemples du dépôt doivent employer des valeurs fictives.

Le panel n’embarque pas de mot de passe root. Un compte root sert à préparer le système ; il n’est pas utilisé pour les opérations quotidiennes du panel.

## Positions et comptes des joueurs

L’administrateur peut voir la position des joueurs connectés : lieu, carte, coordonnées, salon, heure de connexion et âge de la dernière position reçue. Les badges et le nombre d’entrées du Pokédex sont affichés lorsqu’ils ont été transmis. Les joueurs doivent être informés de cette visibilité dans la documentation du jeu.

Une position de plus de dix secondes est estompée ; une position inconnue reste indiquée comme telle. Ces données viennent du suivi du serveur et des informations transmises par les clients. Elles ne donnent pas accès à leurs sauvegardes, à leur équipe complète ou à leur sac.

Les cibles des commandes utilisent l’empreinte du compte, jamais sa clé privée. L’empreinte complète sert à identifier le bon compte pendant l’échange ; seuls ses huit premiers caractères sont affichés dans les listes et le journal. Un bannissement porte sur ce compte et peut être contourné en créant une nouvelle clé. Ce mécanisme ne permet pas d’affirmer l’identité d’une personne.

Conserver les captures contenant des joueurs réels et les exports de journal comme des données privées. Les illustrations publiées dans ce dépôt doivent utiliser exclusivement le mode démonstration.

## Commandes et configuration

Les bannissements demandent la saisie du nom du compte, les fermetures de salon celle du nom du salon, et les modifications de solde celle du montant exact. Les corrections d’Éclats et les remboursements exigent un motif. Un solde négatif est refusé. Les jetons remboursés sont ajoutés à la file de réclamation du compte ; la remise suit ensuite le parcours prévu par le jeu.

Le serveur inscrit les commandes, leurs cibles, motifs et résultats dans son journal, dans la même transaction que la modification des données économiques. L’outil conserve aussi un journal des changements de configuration. Le panel affiche ces entrées dans la partie Administration des Journaux.

Le fichier `eclats.json` est validé avec le parseur réel du jeu avant son remplacement atomique. Son propriétaire et ses droits sont conservés. Le checksum attendu empêche d’enregistrer par-dessus une version différente de celle relue par le panel. Les vingt versions conservées et le journal de configuration restent réservés à root dans le dossier de sauvegardes.

Une écriture réussie ne suffit pas à annoncer que le jeu utilise la configuration. Le panel distingue une version acceptée, une version encore en attente de rechargement et une version refusée. L’acceptation exige un suivi récent du processus du service et le checksum de la version effectivement chargée. Une configuration refusée laisse le jeu utiliser sa dernière version valide. La restauration d’une configuration suit les mêmes contrôles et porte uniquement sur ce fichier.

## Journaux et sauvegardes

L’outil masque les motifs sensibles qu’il reconnaît dans les journaux. Un message inattendu, un nom de joueur ou un secret dans un format différent peut rester visible. Relire toute extraction avant de la publier.

Une sauvegarde téléchargée contient des données privées du jeu. La conserver dans un dossier protégé et éviter de la synchroniser vers un dépôt public. La vérification SHA-256 détecte une copie altérée ; elle ne chiffre pas le fichier.

Les instantanés conservés sur le VPS peuvent inclure des fichiers de configuration privés. Le panel télécharge uniquement la base SQLite extraite de l’instantané. La restauration porte également sur la base ; la configuration courante du serveur reste celle de l’installation.

Une restauration est destructive pour les données actuelles. Elle doit arrêter le service, vérifier la base source et conserver une copie de retour arrière avant de remplacer la base. Le panel demande une confirmation explicite. Après restauration, vérifier les données et une connexion de jeu avant de rouvrir le serveur aux joueurs.

## Limites

Le compte dédié et les commandes fixes réduisent les possibilités d’action depuis le panel. Une clé volée peut tout de même permettre d’arrêter le jeu ou d’accéder aux sauvegardes autorisées. Protéger la session Windows, restreindre l’accès au fichier de clé et révoquer sa clé publique sur le VPS si le PC est compromis.

Avec les nouvelles fonctions, cette clé permet aussi de consulter les positions, de modérer les comptes et de corriger les soldes. Les confirmations protègent contre une erreur de manipulation ; elles ne limitent pas une personne qui possède déjà l’accès SSH du panel.

Le contrôle de disponibilité vérifie le protocole attendu. Il ne garantit pas qu’une partie entière fonctionne, que le tunnel public reste accessible depuis tous les réseaux ou que le VPS ne subira pas de panne.

Les statistiques ouvrent la base en lecture seule. L’activité quotidienne des comptes n’est connue qu’à partir de l’installation du suivi correspondant. Si seule l’activité économique d’une ancienne base est disponible, sa portée est indiquée. Les essais de cette préversion utilisent des données temporaires locales ; ils n’ont pas modifié le VPS de jeu.
