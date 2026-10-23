# Migrer depuis un ancien hôte

Ce guide décrit une bascule vers un VPS sans faire tourner deux serveurs sur des bases divergentes. Les chemins et secrets dépendent de l’installation privée du jeu ; ils ne doivent pas être ajoutés à ce dépôt.

## Préparer

1. Relever la distribution exacte du serveur, sa version Node.js, le chemin de la base SQLite et celui de ses éventuels fichiers annexes.
2. Identifier les services qui l’accompagnent : tunnel Internet, annonce à l’annuaire, tâches de nettoyage, sauvegardes et relance automatique.
3. Conserver une copie hors de l’ancien hôte de la distribution, de la configuration, de l’identité du serveur et des données.
4. Préparer le VPS et une copie de test. Empêcher cette copie de s’annoncer sous l’identité du serveur public ou d’accepter des joueurs.

La copie de test sert à vérifier les dépendances et les adaptations Linux. Elle ne sera pas la copie finale si des joueurs continuent à utiliser l’ancien serveur.

## Effectuer la bascule

1. Prévenir les joueurs de l’interruption, puis arrêter proprement le serveur source.
2. Désactiver sa relance automatique pour éviter un redémarrage pendant le transfert.
3. Créer une sauvegarde SQLite cohérente après l’arrêt. Vérifier son intégrité et conserver cette sauvegarde sur un autre appareil.
4. Transférer cette base finale, la configuration et l’identité nécessaires. Protéger les fichiers privés et retirer les chemins propres à Android ou à Termux.
5. Comparer les comptes, soldes et autres données persistantes attendues. Une base qui passe un contrôle d’intégrité peut néanmoins être la mauvaise base ou une copie ancienne.
6. Démarrer le serveur du VPS, tester les échanges du jeu en local, puis l’accès public.
7. Basculer le tunnel ou l’annuaire si nécessaire et faire une connexion depuis un client de jeu.

Le serveur source reste arrêté. Il ne doit pas redevenir public pendant la validation du VPS.

## Vérifier avant de supprimer

- Réponses `hello` et `ping` du serveur.
- Connexion depuis le client réel et présence du serveur dans l’annuaire prévu.
- Données persistantes comparées à la sauvegarde finale.
- Sauvegarde créée sur le VPS puis téléchargée et vérifiée sur le PC.
- Redémarrage du service et comportement du contrôle de disponibilité.
- Copie de retour arrière disponible hors de l’ancien hôte.

Une fois ces contrôles réussis, retirer seulement les fichiers et mécanismes de démarrage identifiés comme appartenant au serveur. Vérifier les chemins absolus avant toute suppression. Ne pas supprimer globalement le dossier personnel Termux ou les autres projets du téléphone.

Les copies conservées hors du téléphone contiennent des données privées. Elles doivent rester hors du dépôt public.

## Revenir en arrière

Avant l’ouverture aux joueurs sur le VPS, il est possible de revenir à la sauvegarde finale sur l’ancien hôte et d’y rétablir la publication.

Après des parties sur le VPS, l’ancienne base est obsolète. Arrêter d’abord le VPS et exporter sa base actuelle avant un retour : rallumer simplement l’ancien serveur ferait perdre les changements effectués depuis la bascule.
